# Feature 017: webhook retry budget and endpoint recovery

Status: proposed. Extends [Feature 011](./011-webhook-delivery-retries.md).

## Goal and scope

Feature 011 gives a delivery four attempts in just over an hour (5 seconds, 60 seconds, 1 hour). After that it is `failed` until an authorized caller retries it. Events remain stored, but a receiver down for an evening does not recover delivery automatically. There is also no notion of an endpoint being down: five hundred deliveries to a dead receiver spend two thousand attempts, one at a time, and all fail.

This feature lengthens the budget, stops wasting it on an endpoint that is clearly unreachable, and recovers failed deliveries automatically when the endpoint comes back. The receiver contract (duplicates, sequence, order) is unchanged.

## 1. Schedule

- `Webhooks:RetryDelays` becomes configurable with the default `00:00:05, 00:01:00, 00:10:00, 01:00:00, 06:00:00, 1.00:00:00, 1.00:00:00, 1.00:00:00`: nine attempts over about three days and seven hours, excluding endpoint holds and `Retry-After`. The maximum number of attempts per generation is the number of delays plus one. Empty lists, non-positive or decreasing delays, or a sum over 7 days stop startup; equal adjacent delays are valid. The validated schedule is snapshotted per generation so a deployment cannot change a running generation's budget.
- `Retry-After` can still only postpone, now by at most 24 hours per attempt.
- Generation semantics stay: a manual retry or replay starts a new generation with the full budget; attempts are immutable; `outcome_unknown` counts.

## 2. Endpoint reachability

An endpoint gains persisted reachability state and a projection: `healthy`, `degraded`, `unreachable`, or `recovering`, with `consecutive_failures`, `last_success_at` (business delivery), `last_probe_success_at`, `last_failure_at`, `unreachable_since`, `next_probe_at`, probe interval and lease. `unreachable` and `recovering` take precedence over reporting-only `degraded`; an initial `healthy` state with no business success timestamp is not proof of successful delivery. `unreachable_since` denotes the unresolved outage and stays unchanged across `unreachable`/`recovering` transitions until a business delivery succeeds.

- After `Webhooks:UnreachableAfterFailures` (default `5`) consecutive retryable failures across its deliveries, the endpoint is `unreachable`. Non-retryable results (other `4xx`, redirects) do not count; they are the receiver's answer, not its absence.
- While unreachable, the worker sends **one dedicated probe** per interval using the existing signed `webhook.test` payload/receiver contract. The interval starts at one minute and doubles after unsuccessful probes to `Webhooks:ProbeIntervalMax` (default `00:15:00`). Probes are scheduled independently of business deliveries, including when all deliveries are `failed`, none is due, or the queue is empty. Their durable `webhook_probe_attempts` history and lease are separate; they cannot consume a business generation's budget, recursively enqueue retries, or trigger bulk recovery of test events.
- Due business deliveries wait in `retry_wait` with `hold_reason = endpoint_unreachable` and consume no attempt. Holds can extend elapsed delivery time beyond the sum of retry delays; the configured attempt count is not a wall-clock deadline. Probes and all other sends share the existing endpoint one-in-flight lease and global concurrency limit. `Retry-After` on a probe postpones the next probe by up to 24 hours even beyond the interval cap.
- A `2xx` from a dedicated probe or any `webhook.test` changes `unreachable` to `recovering`, records probe success, and leaves business deliveries held. A test success never marks the endpoint healthy, resets its business failure counter, or releases the whole queue. Recovery must first prove acceptance of a real event as described below.
- `degraded` is reporting only: a business success within the last hour and a subsequent business failure while the circuit is otherwise open to delivery.
- A paused endpoint is neither probed nor dispatched and is excluded from active outage alerting. Pause/resume preserves its circuit state, interval and flapping history so toggling pause cannot reset the protection. An unresolved outage resumes through the same probe/recovery gate.

A probe uses current destination/signing configuration and the same destination validation and request timeout as other sends. An unavailable signing key or forbidden destination holds probes with its existing configuration reason instead of consuming network attempts or declaring success. Non-2xx probes leave an unreachable endpoint unreachable. Results from an obsolete destination revision are retained as history but cannot mark the new revision healthy or release its queue.

### Proving recovery with a business event

In `recovering`, allow only one eligible real delivery through the endpoint lease. Prefer the oldest due pending/held retry by `(event.created_at, event.id)` and respect its `next_attempt_at`/`Retry-After`. If none is due, an eligible exhausted failed delivery within the automatic-recovery window may be selected and atomically given one new generation with its full budget. This single trial is allowed before bulk recovery and counts toward its rate limit; it is a normal recorded business attempt, not a free probe. Erased/abandoned events, test events, permanently failed outcomes and unauthorized destinations are never candidates.

On business `2xx`, mark `healthy`, clear the unresolved outage and failure counter, release the held queue and start bulk recovery (section 3). On a retryable failure or `outcome_unknown`, immediately return to `unreachable` without waiting for another five failures; record the attempt and retain its normal retry budget/due time. A permanent failure fails that delivery normally but does not prove recovery: remain `recovering` and try another eligible event no sooner than the current probe interval. This bounds trials if several events return `4xx`.

If there is no eligible business event, remain `recovering` with the queue empty or waiting for its due time; do not fabricate a payment event or claim business recovery. The next eligible delivery is the trial. Continue scheduled test probes at the current interval while waiting; a failed probe returns to `unreachable`, and additional successful tests do not release the gate. Recovery state, trial selection, leases and generation creation survive restart and are serialized against manual retry and other workers.

### Flapping and interval reset

Persist transitions into `unreachable` in a rolling `Webhooks:FlapWindow` (default 1 hour), with `Webhooks:FlapThreshold` (default 3). Test success and entry into `recovering` never reset the probe interval. Returning from a failed recovery trial increases the retained interval as for a failed probe, capped by `ProbeIntervalMax`, while honoring a longer allowed `Retry-After`.

Below the threshold, a business success may reset the interval to one minute. At or above the threshold, keep the increased interval even after a business success and reuse it if delivery fails again. Clear the flapping latch and reset the interval only after `Webhooks:RecoveryStablePeriod` (default 1 hour) continuously healthy without a retryable business failure; persist the stable-period start/reset timestamps. Restart and pause/resume cannot clear the latch or accumulated history. Positive durations and a threshold of at least two are validated at startup.

## 3. Automatic recovery

Only after a real business delivery confirms recovery from `recovering` to `healthy`, business deliveries in `failed` whose last outcome was retryable and whose event is younger than `Webhooks:AutoRecoveryWindow` (default `14.00:00:00`) are re-queued as a new generation, ordered by `(event.created_at, event.id)`, at most `Webhooks:AutoRecoveryRate` (default `60`) per minute, including the single recovery trial described above. Per-subject sequences cannot order unrelated subjects globally. One durable recovery run and its cursor make batching restart-safe; compare-and-update of the failed generation prevents racing manual retry from starting another generation. An audit event `webhook.auto_recovery` records the final count and trigger, including a trial taken from failed deliveries. Recovery batching pauses if the endpoint leaves healthy/degraded delivery operation. Deliveries that failed on non-retryable results stay failed. Erased event bodies, abandoned deliveries and unauthorized/removed destinations are never recovered (Feature 014); paused/configuration-held destinations retain their existing holds.

`POST .../webhook-deliveries/retry-failed` with `Idempotency-Key`, `reason`, optional `from`, `to`, and `event_types`, re-queues up to 1000 failed deliveries of the caller's endpoint in one command and returns the count; it is the manual form of the same thing.

## 4. Visibility

- `GET .../webhook-endpoint` shows `reachability`, the separate business/probe success times, recovery trial ID when present, flapping count/latch, retained interval and next probe time alongside pending, failed and held counts.
- `GET .../webhook-deliveries` gains `hold_reason` and the filter `state=retry_wait&hold_reason=endpoint_unreachable`.
- Failed deliveries older than one hour and unresolved endpoint outages older than 30 minutes appear in the attention list and alerts ([Feature 016](./016-attention-events-and-parent-attention-list.md), [Feature 019](./019-operational-health-and-alerting.md)). Moving to `recovering` after a successful test does not clear the outage or restart its age.

## 5. Acceptance criteria

1. Isolate the retry schedule with an unreachable threshold above the generation's attempt count: a receiver returning `503` exhausts exactly the configured attempts and delays. `Retry-After` postpones by at most 24 hours and never adds an attempt. With circuit protection enabled, holds can extend elapsed time but never consume business attempts.
2. With six deliveries due and the receiver down, five failures make it unreachable; only dedicated probes are then sent, business attempt counters stay unchanged, and probe intervals double to the cap. Equal retry delays are accepted, decreasing/non-positive delays are rejected, and existing generations retain their schedule after configuration changes.
3. A probe/test `2xx` enters `recovering`; only one eligible real event is dispatched and the remaining queue stays held. Only its `2xx` establishes healthy, releases the queue and starts bulk recovery. Retryable/unknown results return immediately to unreachable; permanent failure remains local to the event and the next trial is interval-limited.
4. Deliveries that had already failed with retryable outcomes are re-queued automatically within the window, oldest first, rate limited, and audited; a delivery that failed with `410` is not.
5. A receiver that returns `400` for one event does not make the endpoint unreachable, and that delivery fails at once as today.
6. Bulk retry requires an idempotency key, is scoped to the caller's endpoint, and reports what it queued.
7. Restart-safety: an endpoint in the unreachable state, its probe schedule, and the held deliveries survive a restart; a worker that dies during a probe leaves an `outcome_unknown` attempt and the next probe runs on schedule.
8. The receiver example in `docs/webhooks-receiver.md` still passes unchanged: nothing about the request, signature, or body differs.
9. Seed an unreachable endpoint with only exhausted retryable failed deliveries and no new events. A successful test probe selects one eligible delivery as a new-generation recovery trial; its success unlocks the remaining recovery, without a user call. With an empty queue, remain recovering until the next real event succeeds. Both paths survive restart.
10. Probe success racing manual retry or another recovery worker cannot create duplicate generations. An obsolete destination's success cannot release a changed endpoint; erased/abandoned events never reappear.
11. A receiver returning `2xx` for every test and `503` for business events never releases its whole queue: each successful probe permits one real trial only. Repeated unreachable entries trigger the flapping latch, and restart/pause/resume preserve the increased interval. Also test brief business successes between failures: at the threshold the interval stays increased until the full stable period passes.

## 6. Not included

Per-subject ordered delivery (a failed event blocking later events of the same request), dead-letter export, and receiver-side acknowledgement of batches.
