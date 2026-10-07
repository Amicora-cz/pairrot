# Feature 005: expected payments

## Goal and scope

Save an expected payment so Pairrot knows which tenant expects money, on which bank account, and for which NaLekci reservation. Generate its payment instructions and QR from the saved record using the existing Feature 004 code.

This adds persistence and a reference to the reservation. It does not store QR image files. Incoming messages and transactions are covered by Features [006](./006-inbound-email-reception.md) and [007](./007-bank-message-parsing.md). Matching and outgoing payment-status webhooks come later.

The existing standalone QR endpoints remain available and do not start saving requests implicitly. Users must explicitly create a payment request to track an expected payment.

## 1. Main flow

1. NaLekci selects an instructor or studio and one of its active bank connections.
2. It creates a payment request with its reservation reference, amount, currency, and variable symbol.
3. Pairrot saves the request and returns its ID, initial `open` state, and payment instructions, including a QR payload.
4. NaLekci displays the instructions or obtains the corresponding PNG.
5. NaLekci can retrieve the request by ID or external reference. Repeating creation after a timeout returns the existing request.
6. An administrator can find the same record in the local SPA, inspect its instructions, update its description or due time, and cancel it.

The request remains `open` until a later feature records an allocation or it is explicitly cancelled. Time passing does not mark it paid or automatically cancel it.

## 2. Data and validation

| Field | Rule |
| --- | --- |
| `id`, `tenant_id` | Assigned by Pairrot. Ownership comes from authenticated scope, never from the request body. |
| `bank_connection_id` | Required. Must belong to the owner and be active when a new request is created. |
| `external_id` | Required nonblank string, up to 200 characters, unique within the owning tenant. Identifies a reservation or another expected payment in the calling application. |
| `amount` | Required integer minor units. Reuse the current QR range: 1–999999999. |
| `currency` | Required `CZK`, matching the bank connection. |
| `variable_symbol` | Required string of 1–10 ASCII digits. Supplied by NaLekci in this feature. |
| `message` | Optional recipient message using the current validated QR rules. Frozen after creation. |
| `description` | Optional internal description, up to 1000 characters. Not included in the QR. Editable. |
| `due_at` | Optional ISO 8601 timestamp with an offset, stored in UTC. Editable; not encoded into the QR in this feature. |
| `status` | `open` or `cancelled` in Feature 005; set by the service, not arbitrary client input. |
| `created_at`, `updated_at`, `cancelled_at` | Lifecycle timestamps maintained by Pairrot. |
| `version` | Incremented on changes, for rejecting updates based on stale data. |

Freeze the destination account number and IBAN as snapshots at creation. Store the generated SPAYD payload and its generator version with the request so later renderer changes do not silently alter instructions already issued. PNG images are rendered on demand from that payload.

Use the implemented QR validation, including the behavior recorded in [the Feature 004 banking-app verification](../verification/004-payment-qr.md): preserve supported Czech text, reject `*` and `%`, and count message characters without percent encoding. Do not reintroduce the older encoded-message proposal from the initial Feature 004 document.

### Variable symbols

Keep the supplied string for display and QR generation. Also store a comparison key with leading zeros removed; all-zero values become `0`. In this design, `123` and `000123` reserve the same symbol for a given bank account, preventing two requests from becoming indistinguishable if a bank omits leading zeros.

Enforce uniqueness of the comparison key within the bank connection, including cancelled requests. The same symbol on different bank accounts is allowed. Symbols are not automatically generated or recycled in this feature. Reject a collision rather than silently choosing another symbol.

## 3. Repeated creation

Use `(tenant_id, external_id)` as the creation identity:

- A new reference creates one request and returns `201`.
- The same reference and equivalent original creation data return the existing request with `200` and `replayed: true`.
- The same reference with different original data returns `409 external_id_conflict`.
- A symbol already reserved by a different request returns `409 variable_symbol_conflict`.
- Concurrent requests cannot bypass either uniqueness rule.

Save a canonical fingerprint of the original validated creation input. Compare retries to that fingerprint, not to mutable fields after a later edit. Equivalent omitted optional fields and explicit `null` values should compare equally. Do not normalize away a deliberate change to the displayed VS, message, amount, or target account.

A creation replay does not reopen a cancelled request or undo later edits. It returns the current record with its current state. A replay can still find that record after its bank connection is archived; it does not create a new payable request. Normal authorization and tenant lifecycle checks always happen first.

## 4. Updates, cancellation, and future payment states

- Only `description` and `due_at` can be edited, and only while the request is open.
- Account, owner, external reference, amount, currency, VS, message, and the saved QR payload are immutable.
- Cancel through an explicit operation with an optional reason. Keep the record, symbol reservation, and history.
- Repeating cancellation has no further effect. There is no hard delete or reopening endpoint.
- A cancelled request cannot issue fresh payment instructions or a QR through the request-specific endpoints. Previously copied codes remain ordinary bank payment instructions; Pairrot cannot invalidate a copy already given to a payer.
- `is_overdue` is computed when an open request has a past `due_at`; it is not another stored status.
- Archiving a bank connection does not silently cancel its requests. Historical details remain readable, but fresh instructions and QR generation are blocked until the operator resolves the situation.

Expose `paid_amount = 0` and `remaining_amount = amount` for open requests in this feature. For cancelled requests, return `remaining_amount = null` because no further payment is being requested. These are read-only response values, not a manual “mark paid” control.

The later allocation feature will add `partially_paid`, `paid`, and `overpaid` and calculate paid amounts from allocations. It must prevent cancellation of requests with allocations until those allocations are resolved. Feature 005 introduces no pretend or test-only paid state.

## 5. API

Use the same handlers under the base prefix `/v1/tenant` for an owning tenant and `/v1/subtenants/{subtenantId}` for authorized direct-child management. Append the suffixes below to either prefix.

| Method and suffix | Behavior |
| --- | --- |
| `POST /payment-requests` | Create or replay creation. Return the record and, when payable, saved payment instructions and QR payload. |
| `GET /payment-requests` | Paginated list; filter by exact external reference, status, bank connection, VS, or overdue state. |
| `GET /payment-requests/{id}` | Record, payment summary, state, timestamps, and version. |
| `PATCH /payment-requests/{id}` | Update description or due time. |
| `POST /payment-requests/{id}/cancel` | Cancel an open request. |
| `GET /payment-requests/{id}/payment-instructions` | Return saved destination, payment fields, and QR payload for an open request on an active connection. |
| `GET /payment-requests/{id}/qr.png` | Render PNG from the saved payload, with the same payable-state checks. |

Example creation body:

```json
{
  "bank_connection_id": "<existing-connection-id>",
  "external_id": "booking_123",
  "amount": 45000,
  "currency": "CZK",
  "variable_symbol": "2026000123",
  "message": "Yoga October",
  "description": "Reservation booking_123",
  "due_at": "2026-10-15T18:00:00+02:00"
}
```

Current-tenant access requires a tenant key or administrator session. Parent routes require the parent's management key or an authorized parent session and `can_create_subtenants = true`. A parent can inspect suspended or deleted children for history; mutations and payable outputs require active ownership and ancestry. Unrelated tenants and siblings receive `404` for foreign records.

Browser writes retain antiforgery and expected-tenant checks. GET details return an ETag based on `version`; PATCH and cancellation of an open request require `If-Match`, returning `428` if missing and `412` if stale. A repeated cancellation of an already cancelled record is a no-op. Creation retries use the external-reference rule, not ETags.

Other errors use the existing JSON shape: `400` invalid fields, `401/403` authentication or permissions, `404` out-of-scope resources, and `409` semantic conflicts or an unpayable state. Image endpoints return JSON on errors and successful images are not publicly cached.

## 6. Logical schema and migration

Add `payment_requests` with the fields above, bank-account snapshots, `variable_symbol_key`, `creation_fingerprint`, `qr_payload`, `qr_format`, and `qr_generator_version`. Store monetary values as integers and timestamps with time zones.

Required constraints and relationships:

- Foreign key to the owning tenant.
- A composite relationship from `(tenant_id, bank_connection_id)` to the bank connection's `(tenant_id, id)`, preventing a request from referencing another tenant's account even if application code makes a mistake.
- Unique `(tenant_id, external_id)` and `(bank_connection_id, variable_symbol_key)`.
- Positive amount within the supported range, supported currency, valid state, and cancellation timestamp consistent with state.
- Indexes for tenant-scoped lists by state, due time, and creation time.

Create, edit, and cancel with an audit record in the same database transaction. Ordinary reads and rendering do not create business changes. Keep original creation input fingerprints after edits and cancellation.

No migration of old standalone QR results is possible: they were not stored. Feature 004 calls remain stateless and require no migration.

## 7. Local UI and acceptance

Add an Expected payments list and detail for the selected tenant and permitted children. Support creation, filters, QR preview/download, description and due-time edits, and cancellation. Show external reference, destination, expected amount, paid amount, and state. Make the current owner clear and report stale-edit conflicts without overwriting newer changes.

Verify at least:

1. Create, list, find by external reference, and read the same request after application and database restarts.
2. Saved instructions and PNG decode to the original account, amount, VS, and message, using the existing QR implementation.
3. Repeated and concurrent creation yield one record; a changed original input conflicts. Retries after metadata edits return the existing updated record.
4. Equivalent VS comparison keys collide on one account, but may occur on different accounts.
5. Cancelled requests remain in history and keep their reference and symbol; replay does not reactivate them.
6. Immutable-field changes and forged tenant/bank relationships fail. Parent, sibling, tenant-key, and browser access follow the intended scopes.
7. Overdue calculation does not mark a request paid or cancelled. No email or transaction is needed to create an expected payment.
8. Stale edits are rejected. Cancel, archive, and creation races leave a consistent state.
9. Stateless QR generation still creates no record. A saved request produces no outgoing payment-status webhook yet.

## 8. Boundary with the next features

Feature 005 produces expected payments. Feature 006 produces stored inbound messages. Feature 007 produces normalized incoming transactions. None of these three performs matching; Feature 008 will connect requests to transactions and compute actual paid amounts.

Campaigns, billing, automatic VS generation, allocations, reminders, payment-status webhooks, and production frontend deployment remain deferred.
