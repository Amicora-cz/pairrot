# Feature 012: automatic variable symbol generation

Status: implemented, with annual `YYYYNNNNNN` numbering. Since [Feature 013](./013-bank-account-identity-and-reuse.md) the allocator and symbol uniqueness are per physical bank account instead of per connection.

## Goal and scope

Extend [Feature 005](./005-expected-payments.md) so an API client can create an expected payment with its reservation reference, destination bank connection, amount, and currency, without assigning a variable symbol (VS). Pairrot assigns the VS, saves it with the request, and includes it in the existing response and QR instructions.

Keep caller-supplied symbols supported. This feature changes payment-request creation only; standalone QR generation from Feature 004 remains stateless and never allocates or reserves a symbol. Matching, payment states, and webhook contracts from Features 009–011 remain unchanged.

## 1. Main flow

1. A client creates a reservation and its stable external reference, for example `booking_123`.
2. Its backend creates a payment request for the instructor or studio, omitting `variable_symbol`.
3. Pairrot checks authorization, validates the input, and looks for an existing request with the same `(tenant_id, external_id)`.
4. For a new request, Pairrot assigns an unused VS for the selected bank connection and saves the request, its frozen payment instructions, and its audit record atomically.
5. Pairrot returns the request ID, assigned `variable_symbol`, initial `open` status, and existing payment instructions with the QR payload. The existing PNG endpoint renders the same saved payload.
6. The client stores the request ID alongside its reservation and displays the QR. It does not generate or reconstruct the VS.
7. A retry after a timeout returns the same request and VS. Incoming payments are matched and reported through the existing payment-status webhook flow.

NaLekci may continue to generate its own VS and send it explicitly; its independent generator should use the same `YYYYNNNNNN` format ([main-panel #1082](https://github.com/Amicora-cz/main-panel/issues/1082)). This does not require calling Pairrot for automatic assignment.

NaLekci continues to own reservation capacity, payment deadlines, cancellation policy, and customer confirmation. Generating a VS neither reserves a lesson nor confirms payment.

## 2. Input and API contract

Use the existing `POST /v1/tenant/payment-requests` and `POST /v1/subtenants/{subtenantId}/payment-requests` handlers. No new allocation endpoint or separate pre-reservation call is needed.

| `variable_symbol` input | Behavior |
| --- | --- |
| Omitted or JSON `null` | Assign automatically. These inputs are equivalent for replay. |
| String of 1–10 ASCII digits | Use the caller's exact string, retaining leading zeros and existing validation/uniqueness rules. |
| Empty or whitespace string, non-digits, more than 10 digits, or another JSON type | Return `400`; never silently switch to automatic generation. |

Example creation body for a CZK 450 reservation:

```json
{
  "bank_connection_id": "<existing-connection-id>",
  "external_id": "booking_123",
  "amount": 45000,
  "currency": "CZK",
  "message": "Yoga October",
  "description": "Reservation booking_123",
  "due_at": "2026-10-15T18:00:00+02:00"
}
```

Keep the current response structure. Its `variable_symbol` is always a non-null string for a saved payment request, including automatically generated requests. The record, payment instructions, stored SPAYD `X-VS`, and decoded QR must agree. Return `201` for creation and `200` with `replayed: true` for replay, as in Feature 005.

Retain existing errors and add `409 variable_symbol_space_exhausted` when no automatic symbol remains. A caller-supplied collision still returns `409 variable_symbol_conflict`; Pairrot must not substitute another symbol. A changed original creation input still returns `409 external_id_conflict`.

## 3. Symbol namespace and allocation

- Generate ten ASCII digits in the form `YYYYNNNNNN`. `YYYY` is the creation year in `Europe/Prague`, not the due-date or lesson year; `NNNNNN` runs from `000001` to `999999`. Use 64-bit arithmetic. Examples: `2026000001`, `2026000002`, then `2027000001` after Czech midnight on New Year.
- Allocate monotonically within the current year's series per physical `bank_account_id`, skipping every reserved symbol across all registrations and tenants. Separate physical accounts have independent series.
- Preserve Feature 005's normalized comparison key: `1` and `0001` occupy the same symbol. Caller-supplied zero remains valid, but automatic generation never selects sequence `000000`.
- Retain reservations for every saved request, including paid, overpaid, and cancelled requests. The sequence starts again with the new year prefix, but a complete VS is never reused. Annual exhaustion returns `409 variable_symbol_space_exhausted`; never wrap into the following year early.
- Supplied and generated symbols share the same uniqueness constraint. A supplied high symbol does not advance the automatic cursor; an explicitly supplied current-year symbol is skipped when reached.
- Previously issued symbols, frozen QR payloads and original creation fingerprints remain unchanged. Replays return the original symbol even after a year change or the transition from the legacy `1`, `2`, … format.

The symbol is a payment reference, not an authentication secret. Reads and QR rendering retain the existing authorization rules.

## 4. Repeated creation and original input

Resolve replay before allocating a candidate. Preserve `(tenant_id, external_id)` as the creation identity and compare against the original validated input, not the current mutable description or due time.

Record whether the original creation used `automatic` or `supplied` mode. An automatic request's creation fingerprint describes the automatic intent, not the generated value. Omission and explicit `null` have the same fingerprint. The saved VS is an output of creation and remains immutable.

| Original creation | Retry | Result, assuming other original fields are equivalent |
| --- | --- | --- |
| Automatic | Omitted or null VS | Replay the same request and assigned VS. |
| Supplied `000123` | Supplied `000123` | Replay. |
| Supplied `000123` | Supplied `123` | `409 external_id_conflict`, preserving Feature 005's exact input semantics. |
| Automatic | Explicit VS, even the assigned value | `409 external_id_conflict`: the original mode changed. |
| Supplied VS | Omitted or null VS | `409 external_id_conflict`: the original mode changed. |

Retries must preserve the original creation mode; clients must not copy an automatic response's VS back into the creation body. Use the existing read endpoints to fetch a request by its ID or external reference.

An equivalent replay returns the current record after metadata edits, payment, or cancellation, without resetting its state, creating another audit event, or issuing another VS. Retain the existing lifecycle rules: authorization runs first; replay can find a historical request on an archived bank connection but must not produce fresh payable instructions where they are prohibited.

Concurrent equivalent calls for one external reference produce one request and one committed symbol assignment. Calls with different original data resolve to one creation and a conflict for the other caller; neither can overwrite the winner.

## 5. Persistence and concurrency

Use PostgreSQL as the authority for allocation, shared across processes and restarts. Do not use an in-memory counter, unlocked `MAX(...) + 1`, or a check-then-insert loop without database serialization.

A suitable implementation is one allocator row per physical bank account, with a persisted 64-bit `next_candidate` and a row lock held until the creation transaction commits. Initialize missing rows safely under concurrent first use. Both supplied and automatic creation must participate in the same per-account serialization, including for existing bank connections.

Within that transaction:

1. Recheck replay and required ownership/bank-connection lifecycle conditions before assigning a symbol.
2. For automatic mode, use `next_candidate` when it is within the current year's series or equals its exhausted sentinel (`YYYY999999 + 1`); otherwise start at `YYYY000001`. Find the first unreserved normalized key at or above that candidate and no greater than `YYYY999999`. Skip existing supplied and generated keys using indexed database queries. Do not report exhaustion merely because a bounded collision retry count was reached.
3. Build the frozen bank-account snapshot and QR payload with the chosen VS, then save the payment request and audit record. Preserve existing matching-job scheduling for newly created requests.
4. Advance the cursor beyond the assigned value and commit with the request. For supplied mode, reserve the exact input through the existing unique constraint without jumping the cursor to that supplied value.

Keep database uniqueness on `(tenant_id, external_id)` and `(bank_account_id, variable_symbol_key)` as the final backstop. Handle external-reference races even when competing calls use different bank connections: the loser reloads the winning request and applies the original-input comparison. No constraint failure may leave a partially saved request, QR, or allocator update.

A rollback does not publish or reserve an uncommitted symbol. Return generated instructions only after successful commit. A commit followed by a lost HTTP response is recovered through ordinary replay.

The exhausted cursor for a year is `YYYY999999 + 1`, never an issued VS. Exhaustion returns the documented conflict without a partial request; existing replays and explicit creation with an otherwise unused valid symbol remain possible. Do not reset within the same year to solve exhaustion. At the next year boundary, the new prefix permits a fresh series. The database retains its legacy cursor storage range of `1`–`10000000000`.

## 6. Migration and compatibility

- Add persisted creation mode, for example `variable_symbol_source` with values `automatic` and `supplied`, constrained to valid values. Backfill all existing requests as `supplied`.
- Add allocator state with a foreign key to the physical bank account and a valid cursor range of `1`–`10000000000`. Missing rows are safely initialized with the internal cursor `1`; automatic creation maps legacy or other-year cursors to the current year's `YYYY000001` and skips all historical reservations. Do not seed from the maximum supplied symbol. The annual-format change needs no schema migration and does not rewrite existing rows or fingerprints.
- Keep stored `variable_symbol`, normalized keys, request IDs, external references, QR payloads, and existing creation fingerprints unchanged.
- Preserve the current `v1` fingerprint calculation for supplied mode, including existing rows. Use a distinct versioned fingerprint for automatic mode with an explicit automatic marker and the other normalized original creation fields. Do not recompute historical fingerprints from edited records.
- Existing clients that supply VS retain their behavior and response shape. The creation mode can remain internal; this feature does not require a new response field or webhook schema.
- Deploy the new numbering behavior without mixed old/new writers, so older instances cannot continue issuing plain-number symbols. The original Feature 012 migration and creation fingerprints stay unchanged.

The standalone QR API still treats an omitted VS as absent from the QR. It must not create a payment request, move the cursor, or reserve a symbol. Creation of a saved payment request always produces a VS after this feature.

## 7. Local UI and documentation

Update the existing local payment-request creation form to default to automatic assignment. Keep an explicit option to supply a custom VS; validate that field when selected. Submit omission/null in automatic mode and retain the original mode/body for retries. Show the returned VS and QR in the existing request detail after creation succeeds.

Update API examples and any existing CLI payment-request creation command so omission can request automatic assignment. Do not add a new production frontend or change the standalone QR form's semantics. Mark the feature implemented in the README only after the implementation and verification are complete.

## 8. Acceptance criteria

1. Omitting VS or sending null creates a saved request with a valid generated VS; the response, instructions, saved payload, and decoded PNG contain the same value.
2. Generation on a fresh account in 2026 starts at `2026000001`. Existing `2026000001`, `2026000002`, and cancelled `2026000003` cause the next automatic request to use `2026000004`. A supplied `9999999999` alone does not prevent allocation of `2026000001`. Legacy short symbols and their QR payloads/replays stay unchanged.
3. Supplied VS retains its existing validation, leading zeros, and conflict behavior. Empty/invalid values are rejected without allocating a symbol.
4. Equivalent automatic retries, including omission versus null and a lost response after commit, return the original ID and VS with replay semantics. Retries after edits, payment, cancellation, and connection archival retain Feature 005/009 lifecycle behavior.
5. Changed amount, account, external-reference payload, or creation mode conflicts as specified. Supplied-mode retries of pre-migration records still work, including records whose description or due time was edited.
6. Concurrent automatic creates for different references on one connection receive distinct symbols. Concurrent creates for one reference produce one record; competing supplied and automatic creates cannot claim the same normalized key. Run these checks against PostgreSQL with separate database contexts/connections.
7. Concurrent first use safely initializes one allocator. Concurrent same-reference calls targeting different connections resolve to one creation and one input conflict without orphaned state.
8. Restarts retain the cursor and reservations. An injected failure before commit leaves no published request or partial state; a successful commit followed by a simulated response loss is replayable.
9. Separate physical bank accounts allocate independently; registrations of the same account share reservations. Tenant/parent/sibling permissions and inactive ownership/connection checks remain enforced, with no cross-tenant reads or reservations on unauthorized attempts.
10. Boundary fixtures verify the annual maximum and exhausted sentinel without a million inserts. Exhaustion never wraps; replay and valid unused supplied symbols still work. The following year becomes available at Prague midnight even while UTC is still in December. Repeated year crossings never reuse historical symbols.
11. A generated VS participates in the existing exact-match flow and produces the usual signed `payment_request.updated` event carrying the original external reference. Local fixtures and a test receiver suffice; no real bank transfer or NaLekci production receiver is required for this feature.
12. Standalone QR calls remain stateless. The local request form supports both modes and displays the assigned symbol after creation.

## 9. Completion boundary

NaLekci can create and retry an expected payment using only its reservation reference and payment details, while Pairrot owns automatic VS assignment, persistence, and uniqueness. Caller-supplied symbols remain supported.

Configurable numbering formats, symbol recycling, reservation expiry, refunds, and implementation of NaLekci's receiver are outside this feature. Account identity and reuse are covered by Feature 013. NaLekci's independent generator is tracked in main-panel #1082.
