# Feature 019: operational health and alerting

Status: proposed.

## Goal and scope

Pairrot runs as one container on a shared VPS with `GET /health` returning `{"status":"ok"}` as long as the process answers. Nothing says whether the background worker is alive, whether a job has been stuck for an hour, whether Resend has stopped calling the webhook, whether a receiver has been unreachable all night, or whether the database is reachable at all. The operator learns about an outage from a tenant whose payment never showed up.

This feature adds a readiness check, worker heartbeats, an operator status endpoint, alert conditions with a pluggable sink, request correlation, and optional error tracking. It adds no new product behavior.

## 1. Liveness and readiness

- `GET /health` stays as it is: the process is up.
- `GET /health/ready` returns `200 {"status":"ready"}` or `503 {"status":"not_ready","reasons":[…]}` with reasons such as `database_unreachable`, `migrations_pending`, `worker_heartbeat_stale`, `webhook_worker_heartbeat_stale`. It is unauthenticated, answers from cached checks refreshed every 15 seconds so it cannot be used to load the database, and reveals nothing beyond reason codes. `deploy.ps1` waits for it after a restart and fails loudly if it does not come up.
- A dedicated monitor with bounded check timeouts maintains this in-memory cache independently of the durable job queue and database-backed leases. Startup without a completed check and a cache older than 45 seconds return `503 readiness_check_stale`, never the last successful result indefinitely. Only worker roles required by the configured deployment affect readiness; unexpectedly stopping/disabling a required worker fails it. Optional unconfigured roles are reported as disabled, not stale.

## 2. Heartbeats

Each worker (background jobs, webhook delivery, inbound discovery, matching sweep, retention, reception reevaluation and the operational monitor) registers its task lifetime with a supervisor. An independent 30-second ticker updates `last_beat_at` while that task is alive, including scheduled sleep and an in-progress cycle. Task completion/fault/cancellation stops its heartbeat and marks its state; the ticker must not indefinitely renew a terminated worker. Heartbeats live in memory and are upserted to `worker_heartbeats` when the database is available.

Each row also records worker/instance ID, required/enabled state, `cycle_started_at`, `last_cycle_completed_at`, `next_cycle_due_at`, `last_error_code`, and `last_error_at`. A beat proves task lifetime, not progress. `worker_cycle_stuck` means a running cycle exceeds `Workers:<name>:MaxCycleDuration`; `worker_schedule_delayed` means a new cycle has not begun within `Workers:<name>:StartGrace` (default 2 minutes) of `next_cycle_due_at`. The initial maximum cycle durations are 10 minutes for discovery, matching and reception reevaluation, 30 minutes for retention, and 2 minutes for the monitor and queue polling rounds (job execution uses its own leases). They are configurable positive durations; lease renewals do not count as cycle completion. Required workers with either progress failure make readiness fail with the corresponding reason.

Normal one-hour discovery, ten-minute matching and daily retention sleeps remain healthy because their heartbeat is independent and their next scheduled start is explicit. Checks use the current instance/required roles; rows from stopped deployments cannot mask a local failure.

## 3. Operator status

`GET /ops/status` with `Authorization: Bearer <Ops:Token>` (a separate operator secret, not a tenant key, required to be set for the endpoint to exist) returns one JSON document:

| Section | Contents |
| --- | --- |
| `build` | version, commit, started at, environment |
| `database` | round-trip latency, migrations applied, pending |
| `workers` | the heartbeat rows |
| `jobs` | counts by operation and state, oldest due job age, failed jobs in the last 24 hours, leases older than their duration |
| `inbound` | per instance: last delivery, last discovery run and result, coverage gap, quarantined and unrouted counts, paused addresses, daily volume |
| `parsing` | messages awaiting parse, parse failures in 24 hours, unsupported in 24 hours |
| `matching` | unmatched live transactions, open review cases, oldest unresolved |
| `webhooks` | pending, `retry_wait`, held, failed counts, endpoints by reachability, oldest pending delivery age |
| `alerts` | open alerts (section 4) |

Numbers and codes only: no tenant names, addresses, or message content. The CLI gets `ops status` printing the same document.

## 4. Alerts

The dedicated operational monitor evaluates conditions every minute. Each condition has a stable code, configurable threshold, dedupe rule (one open alert per condition and subject), and a resolve notification. Alerts normally persist in `operational_alerts` (code, subject, opened, resolved, last notified) and appear in `/ops/status` and the CLI.

When the database is unreachable, its alert is sent directly to the configured sink using bounded I/O, without a database write or job lease; an in-memory record limits repeats for the lifetime of the process. Failed remote notification also produces a structured local log. On recovery, the monitor reconciles that record into the alert table and sends a resolve notification. Restart during the outage may repeat or lose in-memory notification history; it must not prevent notification. `/ops/status` returns cached/local health with database-derived sections explicitly unavailable, never misleading zero counts.

Production deployment must configure an independent external monitor of `/health` and `/health/ready`, with a notification path independent of this process and its database. It detects a stopped container, host outage, or frozen monitor; this application cannot promise to report its own complete failure. Deployment documentation records the monitor target, thresholds and operator contact, and verifies it by stopping the container.

Provisioning this monitor is a separate, currently open operations task: [external uptime monitoring](../operations/external-uptime-monitor.md). No external monitor is currently configured on the VPS deployment. Implementing Feature 019's endpoints does not complete that task; production readiness requires both the application checks and verified external monitoring. This specification and the linked task do not themselves deploy a monitor.

| Code | Default condition |
| --- | --- |
| `worker_stale` | A heartbeat older than 2 minutes. |
| `worker_cycle_stuck` | Running cycle exceeds that worker's configured maximum. |
| `worker_schedule_delayed` | Required cycle has not started by its persisted due time plus start grace. |
| `job_queue_delayed` | The oldest due job is older than 15 minutes. |
| `job_failures` | More than 10 failed jobs in an hour. |
| `inbound_silent` | A live instance received nothing by webhook for 24 hours while discovery found messages in that range: the webhook is broken. |
| `discovery_failing` | Two consecutive discovery runs failed, or a coverage gap exists. |
| `inbound_volume` | Daily deliveries per instance over `Alerts:InboundDailyVolume`; this is Feature 008's global protection, moved here. |
| `webhook_endpoint_unreachable` | An unresolved endpoint outage older than 30 minutes, including `recovering` after a successful test probe; only successful business delivery clears it (Feature 017). Paused endpoints are excluded. |
| `webhook_failed_deliveries` | Any delivery `failed` for more than an hour. |
| `database_unreachable` | Readiness has reported it for 1 minute. |
| `lease_expired` | A job or delivery lease expired without an outcome. |

Sinks, chosen by `Alerts:Sink`:

- `log` (default): structured log entries at `Error` for open and `Information` for resolve.
- `webhook`: a signed `POST` of the alert JSON to `Alerts:WebhookUrl`, with the Feature 010 signature scheme and a dedicated secret, so the integrating application can relay it to its own monitoring. It uses the same destination validation as payment webhooks.
- `email`: through the shared `Outbound` configuration from Feature 020 to `Alerts:Email`; requires a verified sending domain. Sending alerts must not depend on the failed durable queue.

Each alert is notified at most once per `Alerts:RepeatInterval` (default 6 hours) while open.

## 5. Correlation, logging, and error tracking

- Every request gets an `X-Request-Id` (a supplied canonical UUID is accepted, otherwise a new UUID is generated), included in the log scope, in error responses as `request_id`, and in audit `details`. Arbitrary header text is never echoed or logged as a correlation ID.
- Production logging is structured JSON on stdout with timestamp, level, category, request ID, tenant ID, job ID; never bodies, headers, e-mail content, keys, signing secrets, raw acting-user IDs or labels (Feature 018). External log storage and retention periods are documented with the deployment; database erasure does not claim to purge external sinks.
- `Sentry:Dsn`, when set, enables the ASP.NET Core SDK with `SendDefaultPii = false`, request bodies off, headers off, breadcrumbs limited to log scopes, and a `BeforeSend` that drops anything containing an API key prefix or a `pwhsec_`/`whsec_` string. Tags: tenant ID, job operation, worker name.
- `Metrics:Prometheus = true` exposes `/metrics` behind the ops token with the counters from section 3.

## 6. Acceptance criteria

1. `/health/ready` is `503` when the database is stopped or a required worker stops; a deliberately optional disabled role does not fail readiness. It recovers on the next successful 15-second check after fresh worker evidence is available, and never queries the database on the request path. Stop the monitor itself: the cached success expires within 45 seconds.
2. A live worker stuck in a cycle keeps a fresh beat but triggers `worker_cycle_stuck`; a terminated task stops heartbeating. Hourly discovery, ten-minute matching and daily retention sleep past two minutes without false alerts. Missing their next due start triggers `worker_schedule_delayed`.
3. `/ops/status` requires the ops token, returns every section with correct counts for seeded data, and contains no tenant name, address, or message text (asserted by a test that seeds all three).
4. Each alert condition opens exactly one alert on its threshold, does not repeat within the repeat interval, and resolves when the condition clears; the `webhook` sink's request verifies with the documented signature, and the `log` sink's entries are structured.
5. `inbound_silent` opens only when discovery proves messages existed; a quiet weekend with no mail opens nothing.
6. Valid UUID request IDs appear in logs, error bodies and audit details; arbitrary/oversized/multi-valued input is replaced without echoing it. Acting-user IDs and labels never reach logs or error tracking.
7. With a DSN configured, an exception reaches the fake Sentry server without the request body, headers, or any secret; without one, nothing is sent.
8. `deploy.ps1` fails when readiness does not come up after the restart and prints the reasons.
9. With the database unavailable, the in-process monitor still sends `database_unreachable` to a fake sink without database or job access, rate-limits repeats, and reconciles/resolves after recovery. Separately, the linked operations task verifies external detection of a stopped container and records the notification evidence before production sign-off; passing application tests does not mark that task complete.

## 7. Not included

Dashboards, tracing, multi-instance coordination beyond the existing leases, and alerts about business facts (a tenant's overdue requests), which belong to the attention list of [Feature 016](./016-attention-events-and-parent-attention-list.md).
