# Feature 016: attention events and the parent attention list

Status: proposed. Extends [Feature 010](./010-payment-status-webhooks.md).

## Goal and scope

Feature 010 sends `payment_request.updated`, including changes to partially paid or overpaid requests, but there are no dedicated transaction, review or reception events and no consolidated attention list. An integrator otherwise lists each tenant's transactions, review cases, connections and deliveries. With hundreds of subtenants that is hundreds of requests per poll, and the person who should act is the tenant, who never opens Pairrot.

This feature adds event types, subscriptions and a paginated parent attention list. Delivery and signing use Feature 010, retries/recovery use Feature 017, and erased payloads obey Feature 014's replay prohibition.

## 1. Event types

The envelope is Feature 010's (`id`, `type`, `schema_version`, `created_at`, `environment`, `tenant_id`, `data`). Every new type carries `data.subject_id`, `data.sequence` (increasing per subject, so the receiver's "apply only a higher sequence" rule works unchanged), and `data.cause`.

| Type | When | `data` besides subject, sequence, cause |
| --- | --- | --- |
| `transaction.recorded` | An accepted, live transaction is recorded and its first matching attempt has completed, so the event already says whether it matched. | `transaction_id`, `bank_connection_id`, `bank_account_id`, `amount`, `currency`, `variable_symbol`, `booked_on`, `occurred_at`, `origin_path`, `handling_state`, `matching: { result, payment_request_id, external_id }` |
| `transaction.updated` | Handling or the matching result changes: allocated, revoked, ignored, restored, auto-matching enabled, review required, marked duplicate, rejected, rematched. | the same, plus `allocation_id` |
| `review_case.opened` | A review case is created. | `review_case_id`, `bank_connection_id`, `issue`, `state`, `reason_code`, `transaction_ids` |
| `review_case.updated` | A member is added, the case is resolved or reopened. | the same, plus `resolution`, `decision_id` |
| `inbound_address.paused` / `inbound_address.resumed` | Feature 008 pause and resume. | `bank_connection_id`, `address_processing_state`, `foreign_count_24h`, `operator_only_resume` |
| `bank_connection.reception_changed` | Readiness from Feature 015 changes state, blocking reasons or warnings. | `bank_connection_id`, `readiness` |

Rules carried over from Feature 010: no sender fields, e-mail content, human-entered reasons or secrets. `external_id` is an arbitrary caller reference, not guaranteed non-personal. Event bytes are stored once and never rebuilt; Feature 014 may purge them and leave a non-replayable metadata tombstone. A cause vocabulary per type is fixed and documented in the receiver guide. Review events for Feature 013 cross-tenant conflicts contain only the receiving tenant's evidence and IDs.

Test and fixture transactions produce events only for test endpoints, as payment changes do. Matching's `not_live_evidence` therefore never reaches a live endpoint as an unmatched receipt.

`transaction.recorded` is emitted for matched transactions too, with `matching.result = matched`, so a receiver that keeps a ledger of receipts gets one. A receiver that only wants payment state does not subscribe to it.

### Durable reception transitions

`bank_connection_reception_states` stores connection/owner, state, sorted blocking reasons and warnings, `sequence`, `checked_at`, `changed_at`, and `next_evaluation_at`. The canonical transition value excludes timestamps and rolling counter values. A per-connection lock protects evaluation, comparison, sequence increment, immutable event insertion and delivery fan-out in one transaction. Re-evaluating identical values never produces another event. Sequences belong to a subject kind and ID; they are not globally comparable across transactions, review cases, addresses and connections.

Mutations that affect reception (parse completion, source verification/removal, address or lifecycle changes, including parent suspension) update the projection in their transaction or atomically enqueue a durable reevaluation job when fan-out is large. A restart-safe sweep runs every minute and evaluates due 30-day expirations and dirty projections; configuration/profile deployment marks affected connections dirty at startup. Async transitions become visible within one sweep interval plus processing time; reads return the stored `checked_at` rather than pretending to be instantaneous. The sweep uses the same evaluator and lock as synchronous writes.

Migration creates a baseline projection at sequence zero without historical deliveries. New connections emit their first state at sequence one with `cause = initialized`. Subsequent causes are `lifecycle_changed`, `address_changed`, `message_processed`, `forwarding_source_changed`, `window_expired`, or `configuration_changed`; a changed projection yields exactly one event even when workers race. Collection barriers from Feature 014 prevent new payloads for erasing tenants.

## 2. Subscriptions

- `webhook_endpoints` gain `event_types`, a list validated against the known types. Existing endpoints and endpoints created without the field get `["payment_request.updated"]`, so nothing changes for a receiver that does not opt in. `webhook.test` is always deliverable.
- `POST` and `PATCH .../webhook-endpoint` accept `event_types`; a change applies to events recorded after it commits. History is explicit replay, as before.
- Scope applies unchanged: a parent endpoint with `own_and_direct_children` receives the children's events of the subscribed types.
- `GET .../webhook-events` and `.../webhook-deliveries` gain a `type` filter.
- Deliveries of different types to one endpoint share its queue, lease, one-in-flight rule, and the backoff of [Feature 017](./017-webhook-retry-budget-and-endpoint-recovery.md).

## 3. The attention list

`GET /v1/management/attention` accepts a management key or a parent administrator session authorized to manage direct children. It includes the parent's own endpoint health plus the managed children's business items. `GET /v1/tenant/attention` returns own items only. Both return what is waiting for a person, oldest first:

```json
{
  "items": [
    {
      "kind": "unmatched_transaction",
      "tenant_id": "…", "bank_connection_id": "…", "subject_id": "…",
      "since": "2026-10-08T09:12:00Z",
      "summary": { "amount": 30000, "currency": "CZK", "variable_symbol": "17", "result": "amount_mismatch",
                   "payment_request_id": "…", "external_id": "booking_123_1", "expected_amount": 45000 }
    }
  ],
  "next_cursor": null
}
```

| `kind` | Definition |
| --- | --- |
| `unmatched_transaction` | Accepted, live, `unallocated`, not ignored, not in an open review case, latest result not `matched`. |
| `review_case` | An open review case. |
| `paused_address` | An address in `paused_foreign_mail`. |
| `reception_not_ready` | A connection whose readiness is `not_ready` for a reason other than `waiting_for_first_message` (opt-in through `kinds`). |
| `unsupported_messages` | A connection with `unsupported_message_count > 0` in the last 30 days. |
| `failed_delivery` | A delivery of the caller's own endpoint in `failed` or `held`. |
| `unreachable_endpoint` | The caller's own non-paused endpoint has an unresolved outage for more than 30 minutes (Feature 017), in `unreachable` or `recovering`, even when no delivery remains pending. A successful test probe alone does not remove it. |
| `overpaid_request` | A request with `paid_amount > amount`; includes allocated receipts, with `excess_amount` in the summary. |
| `overdue_request` | A request with `is_overdue` (opt-in through `kinds`; an integrator that runs its own deadlines will not want it). |

Filters: `kinds` (comma-separated), `tenant_id` (one child), `since`. `limit` up to 200, cursor paging. `GET .../attention/summary` returns counts per kind and per tenant for a dashboard badge.

The list is a query over domain tables and the reception projection, with supporting indexes; there is no separate attention-item store. Cursor order is `(since, kind, tenant_id, subject_id)` to break timestamp ties. For reception items `since` is the projection's `changed_at`; other kinds use their relevant persisted transition timestamp, including the first payment change in the current uninterrupted overpaid interval. Items disappear when their cause is resolved, so there is no "dismiss". Overpaid items remain while the ledger records an excess: this feature neither invents a refund operation nor treats allocation as resolution. Every subject is readable under the same authority; sibling data and foreign transaction evidence remain hidden. Summaries apply identical visibility and kind filters. Erased tenants contribute no business items or copied caller references.

## 4. Acceptance criteria

1. Each transition in the event-type table records exactly one event with a per-subject sequence and eligible owner/parent deliveries, even across retries and crashes. Delivery remains at least once. Attention-only conditions such as overdue requests and failed deliveries do not imply an additional event type; overpayment changes use `payment_request.updated`.
2. An automatic match produces `transaction.recorded` with `matched` and `payment_request.updated`; a partial receipt produces `transaction.recorded` with `amount_mismatch` and the suggested request; manual allocation of it then produces `transaction.updated` and `payment_request.updated`.
3. Payloads contain no sender name, sender account, e-mail content, or free-text reason, verified by a test that seeds all of them.
4. Existing endpoints keep receiving only payment events after the migration; opting in and out applies to later events only; replay respects the current `event_types`.
5. The attention list and summary return the seeded situations across two children for the parent and only own items for a child; a sibling's items never appear; resolving each situation removes its item.
6. Test-evidence transactions and review cases reach test endpoints only.
7. The receiver guide documents every type and cause with a test vector, and the SPA shows the attention list for the selected tenant and, for a parent administrator, across children.
8. A 30-day warning expiry without API reads, profile/configuration deployment, and parent suspension each trigger reevaluation. Concurrent sweep and parse workers produce one transition; a crash cannot commit the projection without its event. Unchanged evaluations produce no event, and migration sends no historical flood.
9. An allocated overpayment remains in both the list and counts until its excess is removed from the ledger; resolving an unrelated review cannot dismiss it. An unreachable endpoint with an empty delivery queue remains visible.

## 5. Not included

Per-event-type signing keys, more than one endpoint per tenant, digest or batched events, e-mail notifications to people, and events for configuration changes (keys, endpoints, forwarding sources), which remain in the audit history.
