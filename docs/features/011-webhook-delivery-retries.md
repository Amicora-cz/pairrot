# Feature 011: simple webhook delivery retries

## Goal and scope

Extend [Feature 010](./010-payment-status-webhooks.md) with a small automatic retry schedule when an outgoing payment webhook cannot be delivered. Keep the same endpoint, event body, signatures, delivery history, and receiver contract.

Signing secrets and their rotation already belong to Feature 010. This feature adds no new secret, external queue, scheduler service, or tenant-specific retry settings. It applies only to outgoing Pairrot webhooks; Resend delivery and inbound-message retrieval keep their own retry rules.

## 1. Schedule

| Attempt | Earliest start |
| --- | --- |
| 1 | Immediately when the delivery is eligible. |
| 2 | 5 seconds after attempt 1 fails. |
| 3 | 60 seconds after attempt 2 fails. |
| 4 | 1 hour after attempt 3 fails. |

There are at most **four attempts per delivery generation: the initial attempt and three retries**. The delays are measured from the preceding failure, not all from event creation. After attempt 4 fails, mark the delivery `failed` and stop automatic attempts. A successful attempt ends the sequence immediately.

Use this fixed schedule for the MVP, without exponential backoff, jitter, or a configuration screen. Keep Feature 010's 10-second request timeout. Due times mean "not before": worker scheduling, pause, or downtime may delay an attempt.

## 2. Which failures are retried

| Result | Action |
| --- | --- |
| Any `2xx` | Mark `delivered`; clear the next due time. |
| Network failure, timeout, `408`, `425`, `429`, or `5xx` | Schedule the next attempt if any remain. |
| Other `4xx` | Mark `failed` immediately. A wrong URL, invalid signature, or rejected payload needs a fix rather than the same automatic request. |
| `3xx` | Mark `failed`; do not follow redirects. |
| Paused endpoint, changed destination, unavailable signing key, or lost authorization | Hold without making an HTTP attempt. Apply Feature 010's hold and recovery rules. |

On a retryable response with a valid `Retry-After`, use the later of the scheduled due time and the receiver's requested time. This does not add attempts. Ignore malformed or overflowing values and retain the ordinary schedule.

Record a stable error code and a readable explanation. Failure to deliver never changes a payment's status or rolls back its allocation.

## 3. Persistence and restart behavior

Use Feature 010's delivery rows and the existing PostgreSQL background worker. Add `retry_wait` to the delivery states if it is not already present. Store the attempt number within the current generation, the next due time, and the outcome of every attempt. Do not implement the delays as in-memory timers or a sleeping HTTP request.

- Reserve and record the next attempt under the delivery's database lease before sending. It consumes one of the four attempts, even if the process then dies and the remote outcome is unknown.
- Commit a completed attempt's outcome, delivery state, and next due time together. Only the current lease holder may commit the result.
- At startup and during normal worker polling, pick up pending and due retry work from PostgreSQL. Restart never resets the attempt counter or creates another event.
- After an expired lease with no recorded outcome, mark that attempt `outcome_unknown`. Treat it as a retryable failure, using lease expiry as its failure time. If it was attempt 4, stop with `failed` rather than sending a fifth attempt.
- If an attempt is overdue after downtime, run the next eligible attempt once. Do not send several requests immediately to "catch up" on missed intervals. Later retries are scheduled from their own failure times.
- Pausing or holding does not consume attempts or reset the budget. Once the hold is resolved, continue the same generation, sending when due. Failed or abandoned deliveries do not restart merely because the endpoint was resumed.

Use one retry owner: the webhook delivery scheduler. HTTP handlers, SDK policies, and the general background-job retry policy must not independently resend a webhook outside this budget. Recovery may reserve an attempt that never reaches the receiver, but must not silently exceed four reserved attempts in a generation.

Keep Feature 010's concurrency limits and destination/authorization checks before every send. A retry of an old destination revision stays held until explicit replay to the current revision. Other endpoints and later payment changes may continue while one delivery waits.

## 4. Identity, signing, and manual recovery

Every attempt keeps the same event ID, delivery ID, event creation time, request sequence, and exact JSON bytes. Generate a fresh signing timestamp and HMAC using the currently active signing keys for each HTTP attempt. Never resend an old timestamp/signature pair or create a new payment-change record for a retry.

The receiver may have accepted a request before a connection timeout or Pairrot crash. Feature 010's event-ID deduplication and sequence checks are therefore still required. Neither the retry count nor a timeout proves that the recipient did not process the payment update.

Reuse the existing retry/replay endpoints:

- After a terminal failure, an authorized manual retry starts a new generation with an immediate attempt and the same three-delay schedule. Keep all previous attempts and the lifetime attempt count.
- Repeated manual requests with the same idempotency key do not create additional generations. A request while work is pending, running, or in `retry_wait` returns the existing operation without resetting its counter or due time.
- Replay of a delivered event or explicit replay to a new destination revision follows Feature 010 and receives its own bounded generation.
- Abandoning a delivery clears future scheduled work and records the reason. An already sent request cannot be recalled.

Synthetic `webhook.test` deliveries use the same schedule, within Feature 010's explicit test permissions. No test attempt changes a reservation.

## 5. API, UI, and migration

No new public endpoint is needed. Extend delivery details with `attempt_in_generation`, `max_attempts = 4`, `next_attempt_at`, and the last safe error, alongside the existing generation and lifetime attempt history.

The local SPA shows, for example, "Attempt 2 of 4 failed. Next attempt at 14:32." After the final failure it shows "Automatic attempts exhausted" and the existing manual retry action. A permanent error instead explains why no automatic retry was scheduled.

Reuse existing delivery and attempt tables; add only missing state/counter fields and indexes for due work. Preserve all event IDs, signatures' key metadata, and attempt history. When upgrading from Feature 010, leave already delivered, failed, and abandoned generations unchanged. Newly queued deliveries and explicitly requested new generations use this policy; do not automatically revive the entire old failure history.

## 6. Acceptance criteria

1. A transiently failing receiver is attempted immediately, then no earlier than 5 seconds, 60 seconds, and 1 hour after successive failures. No fifth automatic attempt occurs.
2. Success on any attempt stops the sequence. Permanent errors and redirects stop immediately. `Retry-After` can delay, but never add or accelerate, an attempt.
3. Restart during a wait preserves its due time and budget. Crash during a send records an unknown outcome and consumes that attempt. Restart after a long outage does not produce a burst of catch-up sends.
4. Multiple workers and concurrent manual retry requests cannot bypass the budget. HTTP and job-level retry handlers do not add hidden sends.
5. Each attempt preserves event/delivery identity and payload bytes but uses a fresh timestamp/signature. A receiver that accepted before a timeout handles the later delivery without repeating its business action.
6. Pause/resume, changed destinations, lost permissions, and abandonment preserve Feature 010's rules and never reset the attempt count implicitly.
7. Manual recovery creates one new generation, retains history, and updates the API/SPA. It does not change the payment's state or create another payment event.

Verify timing with a controllable clock and a fake receiver; automated tests must not wait a real hour. A local receiver test should also demonstrate a failed request followed by a successful retry without requiring Resend or a real bank transfer.

## 7. Completion boundary

Feature 010 supplies signed delivery and manual recovery. Feature 011 adds exactly three automatic retries after the initial attempt, with visible terminal failure and durable restart behavior. More elaborate retry policies, per-tenant tuning, and additional infrastructure are outside this feature.
