# Feature 010: payment-status webhooks

## Goal and scope

Deliver payment changes from [Feature 009](./009-payment-matching-and-review.md) to NaLekci or another tenant's application. The recipient can update its reservation from a signed notification, inspect failed deliveries, and recover after an outage.

Pairrot remains the source of payment state. A receiver being offline does not undo an allocation, delay its commit, or change a paid request back to open. Delivery runs through the existing PostgreSQL jobs and `BackgroundService`; no separate message broker or production frontend is introduced.

This is an outgoing integration from Pairrot to the customer's application. It is independent of the incoming Resend webhook in Feature 006. Resend is not used to send payment-status webhooks.

Feature 010 covers signing secrets, one delivery attempt, history, and explicit manual retry/replay. [Feature 011](./011-webhook-delivery-retries.md) adds automatic retries after 5 seconds, 60 seconds, and 1 hour.

## 1. Subscription ownership and configuration

Allow one webhook endpoint per tenant, with these fields:

| Field | Meaning |
| --- | --- |
| `id`, `tenant_id` | Pairrot-assigned endpoint ID and owning subscriber. |
| `url` | Destination HTTPS URL. Credentials and fragments in the URL are forbidden. |
| `scope` | `own` or `own_and_direct_children`. No arbitrary tenant list or recursive descendants. |
| `state` | `paused` or `active`. A new endpoint starts paused until the receiver has been configured. |
| `environment` | `live` or `test`, set at creation and immutable. |
| `destination_revision` | Incremented when the URL changes. |
| `version` | Optimistic concurrency version for configuration changes. |
| Signing-key metadata | Current and retiring key IDs, creation times, and retirement deadline. Secrets are not returned by ordinary reads. |

The initial NaLekci endpoint belongs to the `nalekci.cz` root tenant with `scope = own_and_direct_children`. It receives changes owned by NaLekci itself and its direct instructor/studio subtenants. This scope requires `can_create_subtenants = true` and the parent's management authority. It does not require campaigns; `can_create_campaigns` stays false.

A subtenant may have its own endpoint with `scope = own`. If both the subtenant and parent subscribe, the event is delivered independently to both. A failure at one does not affect the other. The event's `tenant_id` always identifies the payment owner; the endpoint's owner is the subscriber, which may be its parent.

Current-tenant keys and administrator sessions may manage an `own` endpoint. Configuring or managing a subscription that includes children requires a management key or an authorized parent administrator session. A parent's normal tenant key cannot read child event payloads through webhook-history endpoints. Existing direct-child management routes may configure a child's own endpoint, under the existing Feature 001 permissions.

### Configuration changes

- Pausing stops new HTTP attempts while continuing to record eligible deliveries. Resuming releases that backlog without changing event identities or skipping intermediate changes.
- A URL edit creates a new destination revision. New deliveries bind to it. Pending deliveries for the old revision are held as `destination_changed`; they are not silently sent to the new host. The administrator can explicitly replay selected events to the current revision or abandon old deliveries with a reason. Do not keep sending to the old URL after the edit commits; an already in-flight request may still complete.
- Narrowing scope holds deliveries that are no longer authorized. Expanding scope subscribes to future changes only; historical replay is explicit. Ownership is never transferred by an endpoint edit.
- Changes, pause/resume, secret rotation, replay, and abandonment are audited. No hard deletion of endpoint or delivery history is introduced.

## 2. Which changes produce events

Use Feature 009's immutable `payment_request_changes`. Send one event type, `payment_request.updated`, with a cause and a full payment summary, whenever the recorded payment amount or status changes. This includes:

- The first partial payment, full payment, and overpayment.
- A further allocation that changes the amount but leaves the same status.
- Revocation or reassignment that reduces the paid amount.
- A correction following disputed or duplicate transaction evidence.
- Cancellation of an unallocated request.

Description edits, reads, QR generation, repeated creation, unchanged reparsing, and time passing beyond the due date do not create payment events. This feature does not introduce reservation-created or overdue notifications. A review case with no payment-summary change remains available through the review API, rather than generating a pretend payment change.

## 3. Event contract

Use UTF-8 JSON and an explicit `schema_version`. For example:

```json
{
  "id": "<immutable-event-id>",
  "type": "payment_request.updated",
  "schema_version": 1,
  "created_at": "2026-10-07T12:00:00Z",
  "environment": "live",
  "tenant_id": "<payment-owner-tenant-id>",
  "data": {
    "payment_request_id": "<request-id>",
    "external_id": "booking_123",
    "bank_connection_id": "<connection-id>",
    "sequence": 3,
    "cause": "allocation_created",
    "previous": {
      "status": "partially_paid",
      "paid_amount": 20000,
      "remaining_amount": 25000,
      "overpaid_amount": 0
    },
    "current": {
      "status": "paid",
      "paid_amount": 45000,
      "remaining_amount": 0,
      "overpaid_amount": 0
    },
    "expected_amount": 45000,
    "currency": "CZK"
  }
}
```

Amounts are integer minor units, as in Feature 009, never floating-point currency values. Receivers must use lossless signed 64-bit parsing and checked arithmetic, including when their default JSON number type has less precision. A cancelled request has `remaining_amount = null`.

The sequence increases per payment request, not globally or per endpoint. It is separate from the request's general edit version. Expose the latest `payment_change_sequence` alongside the current summary in the payment-request GET response, read consistently from the same database snapshot.

Use a small stable cause vocabulary: `allocation_created`, `allocation_revoked`, `transaction_review_required`, and `request_cancelled`. Both automatic and manual allocation produce `allocation_created`; detailed authors and review explanations remain in the authorized history API. Reassignment produces separate changes for the affected requests, not one ambiguous combined event.

Do not include raw emails, bank headers, payer names/accounts, signing secrets, or unrestricted audit reasons in the payload. The recipient can fetch authorized details separately. Store the serialized event bytes once from the committed change snapshot. Retries and replays use those exact bytes, ID, creation time, and sequence; never rebuild an old event from current payment state.

Test deliveries use `webhook.test`, `environment = test`, an event ID, and an explicit synthetic marker. They contain no real payment-request or reservation ID and do not create allocations or payment-change records. The receiver must acknowledge them without updating a reservation. An explicitly requested test may bypass the endpoint's paused state only; destination validation, signing, tenant authorization, and lifecycle checks still apply.

## 4. Durable event creation and subscription routing

When a payment change commits, persist its event and the eligible delivery rows in the same database transaction as Feature 009's change journal. If no endpoint is configured, keep the event with no deliveries; payment processing still succeeds. Paused subscriptions receive pending deliveries just like active subscriptions, but dispatch is held.

Capture the subscribers, destination revisions, and applicable scope at commit time. Serialize subscription edits and event routing so a concurrent creation, scope edit, or URL edit cannot leave a gap or route against mixed configuration. Do not rely on an in-memory event callback or on timestamps alone to reconstruct which subscriptions existed.

Eligible real payment subscriptions must also match the event's environment. Do not create a live delivery for a test payment change; enforce that boundary again on explicit replay. Synthetic test events are the separate exception described in section 3.

Each delivery is unique by `(event_id, webhook_endpoint_id)`. The same owner and parent may each have a delivery, but repeated transaction commits, job retries, or creation replays cannot create another delivery for the same endpoint. Queue work durably; also recover unscheduled pending deliveries on startup and during normal worker polling. Outgoing delivery is processed promptly, not on Feature 006's hourly incoming-mail discovery schedule.

Before each send, recheck subscriber permissions, current scope, tenant ancestry, endpoint state, and destination revision. A disabled parent capability or changed ownership must not keep leaking child events from an old queue. Hold delivery when the owner or required ancestry is suspended or deleted, and show the reason. Resume only while current authorization permits it. Archiving a bank connection does not block historical payment corrections when the tenant hierarchy is still active.

## 5. Signing and signing-key rotation

Generate a separate cryptographically random 32-byte signing secret for each endpoint. Return it once as `pwhsec_<base64url-secret-bytes>` with a public key ID. It is not a tenant API key and is not the Resend signing secret.

Pairrot must retain recoverable signing material to calculate signatures. Encrypt it at rest with the application's protected key infrastructure, kept available across restarts. Do not store only a hash. Never return the secret in GET responses, logs, delivery history, or ordinary audit records. The local SPA displays the one-time value in memory only.

Send these headers:

| Header | Value |
| --- | --- |
| `Content-Type` | `application/json; charset=utf-8` |
| `Pairrot-Webhook-Id` | Delivery ID, stable across attempts and explicit replays to this endpoint. |
| `Pairrot-Webhook-Timestamp` | Current attempt's UTC Unix time in seconds. |
| `Pairrot-Webhook-Signature` | `v1,<key-id>,<base64-hmac>`; multiple entries are separated by one space during rotation. |

Define the signing input exactly as the ASCII delivery ID, a dot, the decimal timestamp, a dot, followed by the unchanged raw UTF-8 body bytes. Calculate HMAC-SHA256 using the decoded secret bytes and encode the result with standard Base64. There are no added newlines. Publish fixed test vectors and a small .NET receiver example as part of implementation.

The receiver verifies the signature against the raw body before parsing JSON, uses constant-time comparison, and rejects timestamps more than five minutes from its current UTC time. Validate the required headers and compare against any currently accepted key ID. A later retry gets a fresh attempt timestamp and signature even when the event is old. Event IDs and sequences, rather than signature timestamps, handle duplicate and out-of-order processing.

Rotation creates a new current key and reveals its secret once. By default, sign with both the current and previous key for 24 hours so the receiver can install the new one. After that, stop signing with the old key and remove its recoverable secret while retaining non-secret audit metadata. Permit a zero-length grace period for immediate revocation. Only one retiring key is allowed; another rotation must explicitly revoke it or wait for expiry. Queued deliveries use the keys active at send time, not the keys from event creation.

Configuration and key-rotation responses use `Cache-Control: no-store`. Idempotent retries of a completed secret-creation operation return metadata without regenerating or redisclosing the secret. If the one-time response was lost, use an explicit new rotation.

## 6. Delivery attempts and ordering

Use HTTPS POST with normal certificate validation and a 10-second request timeout. Feature 010 makes one attempt for each delivery generation. It records failures for explicit manual recovery; the automatic schedule belongs entirely to [Feature 011](./011-webhook-delivery-retries.md). Do not apply the inbound-processing or generic HTTP retry policy to outgoing webhook sends.

| Result | Action |
| --- | --- |
| Any `2xx` | Mark delivered. The receiver has durably accepted the event. No particular response body is required. |
| Network failure, timeout, `408`, `425`, `429`, or `5xx` | Mark failed with a retryable classification. Feature 011 later schedules automatic retry. |
| Other `4xx` | Mark failed with status; configuration or receiver behavior needs attention. |
| `3xx` | Do not follow the redirect. Mark failed with a destination error. |
| Disallowed destination, unavailable signing key, or lost authorization | Hold with a specific reason; do not send. |

Keep delivery states separate from payment state: `pending`, `running`, `delivered`, `failed`, `held`, and `abandoned`. Feature 011 adds `retry_wait`. A delivery records its due time, total attempt count, generation, last outcome, and destination revision. An explicit retry starts a new generation with one attempt in Feature 010; prior attempts remain immutable. Repeated retry requests while work is already queued or running reuse the operation.

Claim work atomically with the existing database lease mechanism and record an attempt before sending. Only the current lease holder may commit an attempt result. Do not hold a database transaction open during an HTTP call. In Feature 010, an expired send lease with no recorded outcome becomes `failed` with `outcome_unknown`; pending work that has not reserved an attempt can still resume. A crash after the receiver accepts but before Pairrot records success can cause a duplicate on later manual retry or replay. A timed-out request may also have been accepted by the receiver. Exactly-once delivery is not promised; Feature 011 handles automatic recovery of unknown outcomes within its attempt budget.

Dispatch older pending sequences first where practical, but do not promise ordered arrival or block all later changes behind one failed delivery. A correction can arrive before an older paid event. Retrying or replaying the old event retains its old sequence, so it cannot undo the correction at a compliant receiver.

Bound per-endpoint and global concurrency, with an initial one in-flight request per endpoint. Avoid one failing endpoint starving others. Disabling or pausing stops future attempts but cannot recall an HTTP request already sent.

## 7. Receiver behavior and recovery

Document this receiver contract for NaLekci:

1. Verify signature and timestamp, then validate schema, event type, environment, and tenant mapping.
2. Identify the reservation through the payment owner and `external_id`, retaining `payment_request_id`. External references are tenant-scoped, never globally unique.
3. Persist an event-ID receipt and apply a newer payment summary in one local database transaction. Track the highest applied sequence per Pairrot payment request.
4. If the event ID was already handled, or its sequence is older than or equal to the applied one, acknowledge with `2xx` without repeating business actions.
5. For a newer sequence, apply the full `current` snapshot. It may reduce the paid amount or move a reservation's payment state back from paid to partially paid/open. NaLekci decides the business consequences of that correction.
6. Return `2xx` only after durable acceptance. Slow follow-up work should use the receiver's own queue.

A receiver may durably queue a verified event before business application, including when its reservation is not yet available. It must not return success after silently discarding an unknown reservation. Authentication or schema failure must not change a reservation.

Sequence gaps are possible after late subscription or out-of-order arrival. For current payment state, the newer full snapshot is sufficient. If NaLekci needs every historical transition, fetch Feature 009's ordered change history. It can also read the current request and its `payment_change_sequence` to reconcile; applying that snapshot must use the same monotonic sequence check as webhook handling.

Pairrot exposes failed/held deliveries, attempts, pending counts, and the last successful delivery. An administrator can retry failures after fixing the cause, replay already delivered events, or abandon an unwanted delivery with a reason. None of these actions changes the payment itself.

## 8. Historical replay and endpoint changes

Provide an explicit replay operation accepting up to 100 event IDs and the expected current destination revision. It checks current scope and authority for every event, shows which destination will receive them, and creates or reuses the endpoint's delivery records. Reject the batch atomically if an event is outside scope; do not reveal foreign event details.

For an existing delivery, replay creates a new attempt generation under the current destination revision and keeps its delivery ID and immutable event body. Retain each old attempt's destination revision. A concurrent active attempt prevents a destination switch until it completes or its lease expires; the usual duplicate-delivery risk still applies. Retrying an old revision without an explicit replay returns `409 destination_changed`.

Creating an endpoint or expanding its scope does not automatically send old history. This prevents an unexpected flood and makes the destination of old payment data explicit. Existing Feature 009 changes can be listed and selected for replay. Provide a bounded administrative backfill command with an explicit tenant/request or time range, preview count, and selected endpoint revision for larger histories; use the same authorization and uniqueness rules.

## 9. Destination and environment rules

Endpoint configuration gives the application an outbound network destination, so validate it both when saved and before every connection. Require absolute HTTPS URLs and public destinations. Reject embedded credentials, fragments, loopback/private/link-local/metadata addresses, and equivalent IPv6 or mapped-address forms. Validate all resolved addresses and connect only to a validated address while preserving correct TLS host verification; do not allow DNS changes between validation and connection to bypass the checks. Redirects stay disabled.

Do not accept arbitrary outbound headers, authentication cookies, or caller-supplied signing secrets. Bound request/response handling; the event body has a 64 KiB limit and response reading stops at 4 KiB. Record status, duration, and a bounded escaped response excerpt with sensitive values redacted; never copy response headers such as cookies or authorization into history or logs. Failure to read the response body after receiving a successful status does not turn acceptance into a failure.

Use a separate test endpoint in an isolated development database for local integration. A server-configured Development/Test-only allowlist may permit a specific local HTTP host and port. This exception must be rejected outside those environments and must not grant unrestricted private-network access. Production test events still use the production destination restrictions.

Fixture/test payment changes can be delivered only to test endpoints and retain their test environment. They cannot be sent as live payment updates. The explicit `webhook.test` action may probe a live endpoint, but its synthetic event must never update a reservation.

## 10. API and local SPA

Use these suffixes under `/v1/tenant` and, for managing a child's own subscription, `/v1/subtenants/{subtenantId}`:

| Method and suffix | Behavior |
| --- | --- |
| `POST /webhook-endpoint` | Create the single endpoint, initially paused; return its signing secret once. Conflict if one already exists. |
| `GET /webhook-endpoint` | Configuration, key IDs, health, version, and pending/failed/held counts; no secret. |
| `PATCH /webhook-endpoint` | Change URL, scope, or active/paused state using `If-Match`. URL changes follow revision rules. |
| `POST /webhook-endpoint/rotate-secret` | Rotate with expected version and grace period; reveal the new secret once. |
| `POST /webhook-endpoint/test` | Queue a signed synthetic test to the current revision, even while paused; return `202`. No payment effect. |
| `GET /webhook-events` | Paginated in-scope event snapshots, filterable by payment owner, request, and time; supports replay selection. |
| `GET /webhook-deliveries` and `GET /webhook-deliveries/{id}` | Endpoint-owned delivery state, event, attempts, destination revisions, errors, and next retry time. |
| `POST /webhook-deliveries/{id}/retry` | Retry failed/held work when its cause is resolved, with version and reason. Return `202`; successful deliveries use replay. |
| `POST /webhook-deliveries/{id}/abandon` | Stop future attempts with version and reason; retain history. Already-sent requests cannot be recalled. |
| `POST /webhook-endpoint/replay` | Explicit event IDs, expected destination revision, and reason; return queued delivery IDs with `202`. |

A child's routes cannot read or change the parent's endpoint, secrets, or delivery attempts, even for an event owned by that child. A parent subscriber's history may include direct-child events only while the caller has the required current management authority. Recheck scope on event-body reads as well as sends; summary counts must not expose unauthorized children.

Apply existing tenant lifecycle rules, antiforgery, expected-tenant headers, and foreign-resource `404` behavior. Require versions for configuration and delivery mutations (`428` when absent, `412` when stale), and `Idempotency-Key` for new POST operations using Feature 009's command-receipt pattern. Conflicting key reuse returns `409`. Secret-bearing responses follow the one-time exception described above.

The local SPA adds endpoint settings, the one-time secret display, a test action, and a delivery list/detail with readable outcomes and retry/replay controls. Show the destination and revision before sending old events to an edited endpoint. Never label an endpoint as working merely because a URL is saved; distinguish configured, paused, test accepted, last real success, and current failures.

## 11. Logical schema and migration

| Table/change | Important fields and constraints |
| --- | --- |
| `webhook_endpoints` | Unique tenant owner, scope, state, environment, current destination revision, version, timestamps. |
| `webhook_destination_revisions` | Immutable endpoint/revision and URL, creator and timestamp. Preserve destinations used by old attempts. |
| `webhook_signing_keys` | Endpoint, key ID, encrypted secret, current/retiring state, expiry/revocation metadata. At most one current and one retiring key. |
| `webhook_events` | Event ID, optional Feature 009 change reference, owner, request/sequence when real, schema/environment, immutable serialized bytes and digest. Unique change reference for real events. Synthetic tests are explicitly separate. |
| `webhook_deliveries` | Unique event/endpoint pair, bound destination revision, state, generation, counts, due time, hold/error, version and success time. |
| `webhook_delivery_attempts` | Delivery/generation/attempt number, destination revision, signing key IDs, sent timestamp, start/end/duration, HTTP status, safe response excerpt/error, lease identity. No secrets. |
| Existing jobs, audit, and command receipts | Durable scheduling, manual-action history, and idempotent mutations. |

Preserve Feature 009's event IDs and per-request sequences when materializing existing changes. Historical changes do not gain invented subscribers or automatic deliveries during migration. New payment changes atomically create their event and current subscription deliveries. Index pending work by state/due time and tenant-scoped history by time, request, and outcome.

Payment history is not deleted when delivery succeeds or is abandoned. Attempt retention is a separate future policy; this feature introduces no automatic deletion of failed work or original events. Signing-secret retirement removes secret material without deleting audit metadata.

## 12. Acceptance criteria

1. NaLekci's parent endpoint receives its own and direct-child payment changes with correct owner and reservation reference. Sibling/unrelated access is rejected, and a child's endpoint cannot inspect the parent's delivery history.
2. Partial payment, second partial payment, full payment, overpayment, cancellation, and a correction each produce the correct immutable event. Non-payment edits and creation retries do not.
3. Event, subscriber routing, delivery, and payment commit remain consistent under crashes and concurrent configuration changes. Startup recovers unscheduled pending work without creating duplicates.
4. A real receiver validates the documented HMAC test vectors. Tampered body/headers, stale timestamps, wrong keys, and invalid schema do not change reservations. A fresh retry of an old event is valid.
5. Rotation supports overlapping old/new keys, restart, expiry, and immediate revocation. Secrets never appear in GET, audit, logs, or command replay responses.
6. Timeout, transient response, permanent error, redirect, rate limit, and lease expiry record the correct outcome without an automatic second send in Feature 010. A crash after receiver acceptance followed by a manual retry demonstrates harmless duplicate delivery. Automatic retry timing and exhaustion are tested in Feature 011.
7. An old paid event delivered after a correction cannot overwrite the newer reservation state. Sequence-aware GET reconciliation and duplicates work in the receiver example.
8. Pausing accumulates work; resuming releases it. URL edits hold old destinations until explicit replay, and current authorization is checked before each send and payload read.
9. Retry, replay, and abandonment preserve event identity and attempt history. Concurrent manual requests and worker sends do not create duplicate delivery rows or silently retarget work.
10. Destination validation blocks redirects and private-network/DNS-rebinding paths. Local exceptions are restricted to explicit Development/Test destinations. Test data cannot reach live payment processing.
11. Feature 009's existing history survives migration and can be explicitly replayed. Enabling a new endpoint does not send old history without a replay request.
12. Record a controlled end-to-end check from a Feature 009 payment change through a signed webhook to a test NaLekci receiver, including an outage/manual retry and a later correction. Automated fixtures and a local receiver must work without Resend. If actual NaLekci access is unavailable, report that integration check as pending rather than verified.

## 13. Completion boundary

Pairrot can notify an authorized application of payment changes, expose delivery history, and recover through explicit manual retry/replay without duplicating business actions at a compliant receiver. Together with Feature 009, this provides the flow from a saved payment request to a reservation payment update. [Feature 011](./011-webhook-delivery-retries.md) completes the MVP's automatic recovery after delivery failures.

Multiple endpoints per tenant, arbitrary event subscriptions, email/SMS notifications, campaigns, refunds, and a production frontend remain outside this feature. NaLekci's receiver implementation is a separate repository integration; provide its contract and runnable example here without claiming that external application has already been changed.
