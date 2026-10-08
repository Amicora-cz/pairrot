# Feature 016: attention events and the parent attention list

Status: implemented (see [Implementation notes](#implementation-notes)). Extends [Feature 010](./010-payment-status-webhooks.md).

## Goal and scope

Feature 010 sends `payment_request.updated`, including changes to partially paid or overpaid requests, but there are no dedicated transaction, review or reception events and no consolidated attention list. An integrator otherwise lists each tenant's transactions, review cases, connections and deliveries. With hundreds of subtenants that is hundreds of requests per poll, and the person who should act is the tenant, who never opens Pairrot.

This feature adds event types, subscriptions and a paginated parent attention list. Delivery and signing use Feature 010, retries/recovery use Feature 017, and erased payloads obey Feature 014's replay prohibition.

Reception readiness (the `bank_connection.reception_changed` event, its durable projection, and the `reception_not_ready` and `unsupported_messages` attention kinds) was moved to [Feature 015](./015-supported-banks-and-reception-readiness.md), which is deferred; see [section 6](#6-moved-to-feature-015). This feature does not depend on Feature 015.

## 1. Event types

The envelope is Feature 010's (`id`, `type`, `schema_version`, `created_at`, `environment`, `tenant_id`, `data`). Every new type carries `data.subject_id`, `data.sequence` (increasing per subject, so the receiver's "apply only a higher sequence" rule works unchanged), and `data.cause`.

| Type | When | `data` besides subject, sequence, cause |
| --- | --- | --- |
| `transaction.recorded` | An accepted, live transaction is recorded and its first matching attempt has completed, so the event already says whether it matched. | `transaction_id`, `bank_connection_id`, `bank_account_id`, `amount`, `currency`, `variable_symbol`, `booked_on`, `occurred_at`, `origin_path`, `handling_state`, `matching: { result, payment_request_id, external_id }` |
| `transaction.updated` | Handling or the matching result changes: allocated, revoked, ignored, restored, auto-matching enabled, review required, marked duplicate, rejected, rematched. | the same, plus `allocation_id` |
| `review_case.opened` | A review case is created. | `review_case_id`, `bank_connection_id`, `issue`, `state`, `reason_code`, `transaction_ids` |
| `review_case.updated` | A member is added, the case is resolved or reopened. | the same, plus `resolution`, `decision_id` |
| `inbound_address.paused` / `inbound_address.resumed` | Feature 008 pause and resume. | `bank_connection_id`, `address_processing_state`, `foreign_count_24h`, `operator_only_resume` |

Rules carried over from Feature 010: no sender fields, e-mail content, human-entered reasons or secrets. `external_id` is an arbitrary caller reference, not guaranteed non-personal. Event bytes are stored once and never rebuilt; Feature 014 may purge them and leave a non-replayable metadata tombstone. A cause vocabulary per type is fixed and documented in the receiver guide. Review events for Feature 013 cross-tenant conflicts contain only the receiving tenant's evidence and IDs.

Test and fixture transactions produce events only for test endpoints, as payment changes do. Matching's `not_live_evidence` therefore never reaches a live endpoint as an unmatched receipt.

`transaction.recorded` is emitted for matched transactions too, with `matching.result = matched`, so a receiver that keeps a ledger of receipts gets one. A receiver that only wants payment state does not subscribe to it.

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
| `failed_delivery` | A delivery of the caller's own endpoint in `failed` or `held`. |
| `unreachable_endpoint` | The caller's own non-paused endpoint has an unresolved outage for more than 30 minutes (Feature 017), in `unreachable` or `recovering`, even when no delivery remains pending. A successful test probe alone does not remove it. |
| `overpaid_request` | A request with `paid_amount > amount`; includes allocated receipts, with `excess_amount` in the summary. |
| `overdue_request` | A request with `is_overdue` (opt-in through `kinds`; an integrator that runs its own deadlines will not want it). |

Filters: `kinds` (comma-separated), `tenant_id` (one child), `since`. `limit` up to 200, cursor paging. `GET .../attention/summary` returns counts per kind and per tenant for a dashboard badge.

The list is a query over domain tables, with supporting indexes; there is no separate attention-item store. Cursor order is `(since, kind, tenant_id, subject_id)` to break timestamp ties. `since` is the relevant persisted transition timestamp, including the first payment change in the current uninterrupted overpaid interval. Items disappear when their cause is resolved, so there is no "dismiss". Overpaid items remain while the ledger records an excess: this feature neither invents a refund operation nor treats allocation as resolution. Every subject is readable under the same authority; sibling data and foreign transaction evidence remain hidden. Summaries apply identical visibility and kind filters. Erased tenants contribute no business items or copied caller references.

## 4. Acceptance criteria

1. Each transition in the event-type table records exactly one event with a per-subject sequence and eligible owner/parent deliveries, even across retries and crashes. Delivery remains at least once. Attention-only conditions such as overdue requests and failed deliveries do not imply an additional event type; overpayment changes use `payment_request.updated`.
2. An automatic match produces `transaction.recorded` with `matched` and `payment_request.updated`; a partial receipt produces `transaction.recorded` with `amount_mismatch` and the suggested request; manual allocation of it then produces `transaction.updated` and `payment_request.updated`.
3. Payloads contain no sender name, sender account, e-mail content, or free-text reason, verified by a test that seeds all of them.
4. Existing endpoints keep receiving only payment events after the migration; opting in and out applies to later events only; replay respects the current `event_types`.
5. The attention list and summary return the seeded situations across two children for the parent and only own items for a child; a sibling's items never appear; resolving each situation removes its item.
6. Test-evidence transactions and review cases reach test endpoints only.
7. The receiver guide documents every type and cause with a test vector, and the SPA shows the attention list for the selected tenant and, for a parent administrator, across children.
8. An allocated overpayment remains in both the list and counts until its excess is removed from the ledger; resolving an unrelated review cannot dismiss it. An unreachable endpoint with an empty delivery queue remains visible.

## 5. Not included

Per-event-type signing keys, more than one endpoint per tenant, digest or batched events, e-mail notifications to people, and events for configuration changes (keys, endpoints, forwarding sources), which remain in the audit history.

## 6. Moved to Feature 015

These parts were specified here originally and now belong to [Feature 015](./015-supported-banks-and-reception-readiness.md); they are built with it, on top of this feature's event and subscription mechanism:

- The event type `bank_connection.reception_changed` (readiness changes state, blocking reasons or warnings), `data`: `bank_connection_id`, `readiness`.
- The attention kinds `reception_not_ready` (readiness `not_ready` for a reason other than `waiting_for_first_message`, opt-in through `kinds`) and `unsupported_messages` (`unsupported_message_count > 0` in the last 30 days). Their `since` is the projection's `changed_at`.
- The durable reception projection below, and the acceptance criterion: a 30-day warning expiry without API reads, profile/configuration deployment, and parent suspension each trigger reevaluation; concurrent sweep and parse workers produce one transition; a crash cannot commit the projection without its event; unchanged evaluations produce no event, and migration sends no historical flood.

### Durable reception transitions

`bank_connection_reception_states` stores connection/owner, state, sorted blocking reasons and warnings, `sequence`, `checked_at`, `changed_at`, and `next_evaluation_at`. The canonical transition value excludes timestamps and rolling counter values. A per-connection lock protects evaluation, comparison, sequence increment, immutable event insertion and delivery fan-out in one transaction. Re-evaluating identical values never produces another event. Sequences belong to a subject kind and ID; they are not globally comparable across transactions, review cases, addresses and connections.

Mutations that affect reception (parse completion, source verification/removal, address or lifecycle changes, including parent suspension) update the projection in their transaction or atomically enqueue a durable reevaluation job when fan-out is large. A restart-safe sweep runs every minute and evaluates due 30-day expirations and dirty projections; configuration/profile deployment marks affected connections dirty at startup. Async transitions become visible within one sweep interval plus processing time; reads return the stored `checked_at` rather than pretending to be instantaneous. The sweep uses the same evaluator and lock as synchronous writes.

Migration creates a baseline projection at sequence zero without historical deliveries. New connections emit their first state at sequence one with `cause = initialized`. Subsequent causes are `lifecycle_changed`, `address_changed`, `message_processed`, `forwarding_source_changed`, `window_expired`, or `configuration_changed`; a changed projection yields exactly one event even when workers race. Collection barriers from Feature 014 prevent new payloads for erasing tenants.

## Implementation notes

- **Recording.** A save interceptor records the events from the changes being saved, in the same database transaction (opening one for a save that has none), so every code path that changes a transaction, a review case, or an address's processing state produces its event. Each subject gets at most one event per save; its sequence is the subject's highest stored sequence plus one, guarded by a unique index on `(subject_id, sequence)`. Payment events keep their per-request sequence with the request as subject. Events of an owner behind Feature 014's barrier are not recorded.
- **Transactions.** The first event of a transaction is `transaction.recorded`: when its first matching attempt has completed, or at once when it cannot be matched automatically (review required, a Feature 013 movement conflict, already handled). This includes transactions that need review (`matching.result` `review_required`) and test evidence (`not_live_evidence`, a test event). Later events are `transaction.updated` when the handling state, the matching result or its request, automatic matching, the ingestion status, the manual confirmation, the movement conflict, or a new review-case membership changes. Causes: `recorded`, `allocated`, `revoked`, `ignored`, `restored`, `auto_matching_enabled`, `review_required` (also for an allocation revoked because the transaction lost eligibility), `review_decided` (confirmed by a review, or a rejected or duplicate record made allocatable again), `marked_duplicate`, `rejected`, `rematched`. `matching.payment_request_id` is the active allocation's request, otherwise the suggested one.
- **Review cases** carry the IDs of their member transactions (all of the owner's). Causes: `opened`, `member_added`, `evidence_changed`, `resolved`, `reopened`; `decision_id` is the decision made in that change. A case is a test event when any of its transactions or messages is test or fixture evidence.
- **Addresses.** Causes: `foreign_mail_limit`, `resumed_by_tenant`, `resumed_by_operator`. `foreign_count_24h` counts foreign messages of the last 24 hours. The tenant and operator resume now lock and update the row instead of a bulk update, so the change and its event are saved together.
- **Attention list.** One SQL query over the domain tables (`UNION ALL` per kind), keyset paging by `(since, kind, tenant_id, subject_id)` with an opaque cursor. `since`: a transaction's recording or its latest revocation; a case's opening or latest reopening; the pause; the delivery's last state change; the first payment change of the current overpaid interval; the due time. A manually confirmed receipt counts as accepted. The management list also includes the caller's own business items; a failed delivery's `tenant_id` is the event's owner. Deleted tenants are excluded like erased ones.
- **Not built yet:** `unreachable_endpoint` needs Feature 017's outage state and arrives with it.
