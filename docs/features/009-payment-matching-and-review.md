# Feature 009: payment matching and manual review

## Goal and scope

Connect [expected payments from Feature 005](./005-expected-payments.md) to incoming transactions from [Feature 007](./007-bank-message-parsing.md), including notifications accepted through [Feature 008](./008-forwarded-bank-notifications.md). Calculate how much has been paid for each request and let an authorized user resolve payments that cannot be matched automatically.

The main result is a payment request whose status and amounts reflect its recorded receipts. NaLekci can read that result through the API. [Feature 010](./010-payment-status-webhooks.md) will deliver changes through outgoing webhooks; this feature records the changes durably but does not send them.

Use the existing API, PostgreSQL, background jobs, and local Vite + React SPA. No production frontend is added. Campaigns remain disabled. No bank transfer, refund, currency conversion, or accounting document is created.

## 1. Main flow

1. NaLekci creates a payment request with its reservation reference, account, amount, currency, and VS.
2. Pairrot receives and processes a bank notification into an incoming transaction.
3. A background job checks whether that transaction has one exact, eligible payment request.
4. If it does, Pairrot allocates the whole transaction and recalculates the request in one database transaction.
5. Otherwise, Pairrot shows why it could not match and any relevant request candidates.
6. An authorized user can review the evidence, allocate the payment manually, ignore an unrelated receipt, or correct a previous decision.
7. Every change preserves its author, reason, and history. The request API returns the current result immediately after a committed change.

An allocation is the recorded link between a transaction and a payment request. It is not a new movement of money.

## 2. Automatic matching

Automatic matching requires all of the following:

- The transaction is accepted under Features 007/008, has no unresolved review or duplicate conflict, and has not been ignored, rejected, or marked as a duplicate.
- It has no active allocation and automatic matching has not been blocked by a previous manual decision.
- The tenant, its required ancestry, and the bank connection are active. Requests and transactions belong to the same tenant and bank connection.
- Currency matches exactly. The initial currency is CZK.
- The transaction has a valid VS. Compare the normalized keys from Feature 005: `000123` equals `123`; a missing VS is not `0`.
- There is exactly one corresponding request. It is `open`, not cancelled, and has no active allocations.
- The transaction amount equals the full expected amount.

Allocate the entire transaction. Do not infer matches from payer name, message text, similar amounts, or a confidence score. A request past its due date remains eligible; time passing alone does not cancel or expire it.

Feature 008's accepted direct, automatic-forward, and manual-forward paths follow the same matching rules. Keep their origin evidence visible. A manual forward accepted by Feature 008 is different from a user overriding an unresolved review in this feature; the latter requires a separate manual allocation.

| Situation | Outcome |
| --- | --- |
| Exact account, currency, VS, and amount; eligible open request | Allocate automatically. |
| Same VS, different amount | Leave unmatched and suggest the request with `amount_mismatch`. |
| No VS or no request with that VS | Leave unmatched with `missing_variable_symbol` or `request_not_found`. |
| Request already partially paid, paid, or overpaid | Suggest for manual review; do not allocate a top-up or a second receipt automatically. |
| Request cancelled | Show `request_cancelled`; do not reopen it. |
| Conflicting transaction evidence or suspected duplicate | Hold for review; do not count it as paid money. |
| Inactive ownership or archived connection | Hold new allocation with the lifecycle reason. |
| Different tenant, account, or currency | Never allocate. Do not expose another tenant's records as candidates. |
| Multiple eligible candidates despite the request uniqueness rules | Record a conflict and require review; never choose the first row. |

Candidate results are explanations, not reservations. Recheck all rules when committing an allocation. Suggestions are restricted to the same owner, account, and currency; manual search may use external reference, VS, or amount within that scope.

### When matching runs

Queue matching when an eligible transaction is created and when a new payment request is created. This covers a bank notification arriving before the request. Repeated creation of the same request does not create duplicate work.

Reuse Feature 006's durable jobs and lease rules. On installation, schedule existing eligible records in bounded batches. On restart, resume saved jobs. Provide an explicit retry for an unmatched transaction, but a retry never bypasses review or a manual matching block.

Do not repeatedly queue unmatched records in a tight loop. Retry technical failures with the existing bounded policy; a missing request or amount mismatch is a completed business result, not a failed job. Lifecycle reactivation can schedule held work after rechecking all restrictions. Undoing an allocation must not silently schedule the same match again.

## 3. Allocations and payment amounts

One transaction can have at most one active allocation, always for its full amount. Several transactions can pay one request. Splitting a transaction across requests is deferred.

Store allocations as history: an active allocation may become revoked, but is never deleted or silently moved. Reassigning means revoking the old allocation and creating another one. There is no API field that directly sets a request to `paid` or edits `paid_amount`.

For a non-cancelled request:

- `paid_amount` is the sum of active allocation amounts.
- `remaining_amount` is `max(amount - paid_amount, 0)`.
- `overpaid_amount` is `max(paid_amount - amount, 0)`.
- `is_overdue` is true only when the due time has passed and `remaining_amount > 0`.

| Paid amount | Request status |
| --- | --- |
| Zero | `open` |
| Greater than zero and less than expected | `partially_paid` |
| Equal to expected | `paid` |
| Greater than expected | `overpaid` |

Cancellation is an explicit lifecycle decision. A cancelled request has no active allocations, `paid_amount = 0`, `remaining_amount = null`, and `overpaid_amount = 0`. Reject cancellation while any allocation remains active, including a race with a background match. Revoked historical allocations do not prevent cancellation. A cancelled request cannot receive an allocation or be reopened in this feature.

Use checked signed 64-bit integer arithmetic in minor units for allocations and totals. Incoming receipts may exceed the QR amount limit. Reject an operation that would overflow a total without partially saving it.

Example: a request for CZK 450 receives CZK 200. It stays unmatched until manually allocated, then becomes `partially_paid` with CZK 250 remaining. A second CZK 300 receipt is also allocated manually: the result is `overpaid`, with CZK 500 paid and CZK 50 extra. No refund is sent.

### Existing request behavior

This feature extends Feature 005's response states. Preserve IDs, external references, original creation fingerprints, banking snapshots, and original payment instructions. Creation replay returns the current payment state without resetting it.

Allow description and due-time edits for `open` and `partially_paid` requests, using the existing ETag rules. Other payment fields remain immutable. Paid, overpaid, and cancelled requests are read-only apart from explicit allocation corrections.

The existing request-specific payment-instructions and PNG endpoints remain available only for `open` requests that pass the lifecycle checks. For `partially_paid`, `paid`, or `overpaid`, return `409 payment_instructions_not_available`. Do not return the original full-amount QR as if it requested the outstanding balance. Original instruction data stays visible as history in the request detail. Generating a new remaining-balance QR is deferred; Feature 004's standalone QR endpoint remains available and stateless.

## 4. Manual allocation, ignoring, and correction

Manual actions require a nonblank reason and record the authenticated user or API-key identity, owner, and time.

- **Allocate:** choose one eligible transaction and a non-cancelled request with the same tenant, bank connection, and currency. Missing or different VS and an amount mismatch are allowed. Show the resulting paid, remaining, and overpaid amounts before submission in the SPA. A fully paid request may receive another manually confirmed payment and become overpaid.
- **Revoke allocation:** retain the allocation with its revocation author, time, and reason. Recalculate the request, which may become partially paid or open again. Block automatic matching of the released transaction until an explicit decision enables it.
- **Reassign:** revoke first, then allocate to the correct request. Each operation is durable and visible; failure of the second leaves the transaction unallocated and blocked from automatic matching.
- **Ignore:** exclude an unallocated, otherwise eligible transaction from matching with a reason such as an unrelated receipt. Ignoring does not change the original bank facts or source trust.
- **Restore ignored:** return the transaction to manual handling. Do not match automatically until a separate `enable-auto-matching` action passes current eligibility checks.
- **Enable automatic matching:** explicitly remove a manual block and queue a fresh attempt. This cannot override a review, duplicate, rejected state, inactive owner, or manual-only approval.

Never allocate an unreviewed candidate merely because its amount matches. The review process below must establish eligibility first. Manual allocation cannot bypass owner, account, currency, or lifecycle restrictions.

## 5. Review of source evidence, duplicates, and disputes

Create a review case for unresolved Feature 007 candidates and transaction conflicts. Each case links immutable parse runs, the affected transactions, reasons, and the versions being reviewed. Keep cases separate from ordinary matching failures such as a missing VS.

An authorized tenant or parent administrator may record these decisions through the same protected API used by the SPA:

| Decision | Behavior |
| --- | --- |
| `confirm_payment` | Confirm one complete candidate as an actual booked receipt after checking supporting evidence. Create or retain its transaction and make it eligible for manual allocation. |
| `confirm_distinct` | Confirm that the listed lookalike records are separate actual transfers. Retain their IDs and allow manual allocation of each confirmed receipt. |
| `mark_duplicate` | Choose one canonical transaction for compatible copies of the same receipt. Exclude the other records from allocation and preserve their links to the canonical record. |
| `keep_existing` | Resolve a reprocessing dispute in favor of the existing immutable payment facts, with a recorded reason and supporting check. Make the retained receipt eligible for manual allocation. |
| `reject` | Exclude a candidate or transaction as not a valid receipt, preserving the reason and evidence. |

Every approval requires a human-readable explanation of the check performed, for example checking the bank account statement. Record it as `manual_confirmation`, not as passing DMARC or proof that the bank authenticated the email. Preserve the original authentication results and Feature 008 origin path. This is an explicit business decision by the payee or its authorized parent, not a change to sender policy or the forwarding-source list.

Approval cannot invent required payment facts. A candidate must contain an unambiguous positive amount, CZK currency, booking date, and sufficient account evidence consistent with the routed connection. Missing or contradictory critical facts, a different account, a debit, or a correction that has not been resolved cannot be approved through a general bypass. Fix the parser and reprocess the source, or keep the case unresolved. Creating arbitrary transactions or editing their amount in a review form is out of scope.

Duplicate decisions operate on the whole listed conflict set under version checks. All records must belong to the same tenant, account, and currency. A canonical record must have valid complete facts; incompatible amounts cannot be merged. Do not declare records with distinct proven bank references duplicates, or records with the same proven bank reference separate payments. Keep the original records and source links, prevent canonical-link cycles, and never overwrite a transaction with another record's values.

Closing a duplicate-only case removes only that reason. Any other unresolved trust, content, or account issue still blocks allocation. A rejected or duplicate record is not an ignored record and cannot be restored through the ignore endpoint. An authorized user may reopen a review with a reason; a new resolution preserves all previous decisions.

Decisions apply to the exact reviewed evidence and members. Reprocessing unchanged evidence must not recreate a resolved case or undo its decision. New conflicting facts or another suspected copy require a new or reopened case and a fresh eligibility check. Review writes include the versions of any allocations and payment requests they would change.

### A problem discovered after allocation

If parsing, new duplicate evidence, or reopening a review removes a transaction's eligibility, revoke its active allocation and recalculate the affected request **in the same database transaction** as the eligibility change. Apply this even when the owner or connection is inactive: it corrects existing totals rather than creating a new allocation.

Record `transaction_review_required` as the revocation reason and block automatic reallocation. The request detail shows the related open review case separately from its recalculated payment status. Never continue showing disputed money as paid while waiting for a later cleanup job. Existing Feature 007 writes must call this shared consistency rule when Feature 009 is installed.

Resolving the review does not revive old allocations. A user must allocate the approved receipt again. This also prevents a manually revoked match from returning after an unrelated parser retry. Unchanged reparsing with no new conflict keeps existing allocations and produces no payment change.

## 6. Consistency, retries, and change history

Use a database transaction for allocation or revocation, transaction handling state, recalculated request totals/status, audit records, and payment-change records. Lock and recheck affected transactions and requests in a consistent order. The one-active-allocation constraint is enforced in PostgreSQL as well as in application code. Review operations lock every affected record; cancellation and matching use compatible locking.

Expose versions for transactions, allocations, review cases, and payment requests. Manual writes carry the relevant versions shown to the caller. A changed request total, transaction eligibility, or review membership must cause a stale-write conflict rather than applying an old decision. Return `428` for a missing required version and `412` for a stale one. Authorization and ownership checks precede conflict details.

Require `Idempotency-Key` on the new manual POST operations. Store its owner scope, action, canonical input fingerprint, outcome, and created resource IDs in the same transaction. Retrying the same completed command returns the original outcome without repeating the mutation, even if versions have since advanced; the client can then GET current state. Reusing the key with different input returns `409 idempotency_key_conflict`. Failed uncommitted attempts do not consume the key. Recheck authorization before returning a stored outcome. Background jobs use durable operation identities and leases instead of client keys.

Record a payment change whenever allocations change a request's paid amount or status, including changes within the same status such as a second partial payment. Use an immutable event ID and a monotonically increasing sequence per request. Save before/after status and amounts, expected amount, currency, external reference, tenant/request IDs, cause, actor, and timestamp. Include cancellation in this journal as well; description-only edits do not create a payment change.

These records provide the durable input for Feature 010. No webhook endpoint configuration, signing secret, HTTP delivery, or delivery retry is added here. A failed later webhook must never undo an allocation.

## 7. API and permissions

Use the same suffixes under `/v1/tenant` and `/v1/subtenants/{subtenantId}`.

| Method and suffix | Behavior |
| --- | --- |
| `GET /transactions` and `GET /transactions/{id}` | Extend existing responses with handling state, auto-matching block, current allocation, review links, and version. Filter by matching/review state. |
| `GET /transactions/{id}/matching-candidates` | Same-scope requests with reasons and proposed resulting totals; no mutation. |
| `POST /transactions/{id}/match` | Queue or reuse a matching job; return `202`. Review and manual blocks remain effective. |
| `POST /transactions/{id}/allocations` | Body: `payment_request_id`, `expected_transaction_version`, `expected_request_version`, `reason`. Allocate the full transaction; return `201` and updated summaries. |
| `POST /allocations/{id}/revoke` | Require allocation, transaction, and request versions plus reason. Return updated summaries. |
| `POST /transactions/{id}/ignore`, `/restore`, `/enable-auto-matching` | Explicit handling actions with transaction version and reason. |
| `GET /review-cases` and `GET /review-cases/{id}` | Paginated open/resolved cases, evidence, affected records, and decision history. |
| `POST /review-cases/{id}/resolve` | Decision, reason, case version, affected-record versions, and canonical transaction ID when required. Return the saved decision and affected summaries. |
| `POST /review-cases/{id}/reopen` | Reopen with versions and reason; apply allocation revocation atomically when eligibility is lost. |
| `GET /payment-requests/{id}/allocations` | Active and revoked allocations, with method, author, reason, and time. |
| `GET /payment-requests/{id}/changes` | Paginated immutable payment-change history, ordered by per-request sequence. |

Extend payment-request details with calculated amounts, the new statuses, review links, and current version. Keep Feature 005's ETag contract for its existing edit and cancellation operations. New multi-record writes use explicit expected versions in the body. Allocation conflicts such as an already allocated transaction return `409` with a stable reason; foreign resources return `404`.

Current-tenant routes accept tenant API keys or administrator sessions. Direct-child routes require the existing parent management authority and `can_create_subtenants = true`. The same authority may perform manual review, and the audit must identify the actual caller. No global or sibling access is introduced. Browser writes require antiforgery and the expected-tenant header.

User-triggered changes require active ownership, ancestry, and bank connection. Historical allocations, reviews, and changes remain readable under existing history permissions. System corrections that remove invalid allocations follow section 5 even for inactive records. There is no cross-account or cross-tenant reassignment endpoint.

## 8. Logical schema and migration

| Table/change | Important fields and constraints |
| --- | --- |
| `payment_allocations` | Tenant, bank connection, transaction, request, full amount, currency, `automatic`/`manual` method, reason, creator, created time, active/revoked state, revocation details, version. Partial unique index on transaction ID for active rows. |
| `payment_requests` | Add payment summary and change sequence; extend status constraints. Summary is derived from allocations and updated atomically. Preserve original input and QR snapshots. |
| Transaction handling | Auto-matching block and reason, ignored state/details, rejection or canonical-duplicate decision, current review references, manual-confirmation provenance, version. Keep ingestion outcome and origin evidence separately. |
| `payment_review_cases` and case members | Tenant/connection, issue, state, immutable evidence references, affected records, and version. A new conflicting member changes the case version. |
| `payment_review_decisions` | Immutable action, actor, reason, case/evidence versions, affected records, canonical ID if relevant, and timestamp. Reopening appends history. |
| `payment_request_changes` | Unique event ID and `(payment_request_id, sequence)`, before/after summaries, owner, external reference, cause, actor, timestamp. No delivery state yet. |
| Manual command receipts | Unique owner-scoped idempotency key, action/input fingerprint, committed result, and referenced IDs. No automatic expiry in this feature. |
| Existing jobs and tenant audit | Matching/backfill jobs and audit entries for every manual action, including approvals, revocations, ignores, and retries. |

Use composite owner/account relationships so both sides of an allocation belong to the same tenant and bank connection. Currency and full-transaction amount are rechecked under lock; enforce them at the database layer where practical. An active allocation cannot refer to a duplicate, rejected, or unresolved transaction. All paths changing eligibility must preserve this invariant.

On migration, existing requests start with no allocations and their existing open/cancelled status. Create review cases for unresolved existing parser outcomes and transaction conflicts without duplicating cases on restart. Then schedule eligible existing transactions and requests for matching in batches. Existing cancelled requests remain cancelled.

Test and fixture evidence must never pay live requests. Production matching rejects transactions marked as fixture or test-environment data. A development-only setting may enable matching in an isolated local test database; preserve test provenance on allocations and changes. This setting must be refused outside Development/Test and must not promote records to live evidence.

## 9. Local SPA and acceptance

The local SPA adds a matching view with unmatched transactions, request candidates, and clear reasons. Before manual allocation, show account, source origin, expected amount, receipt amount, and resulting payment summary. Request details show active/revoked allocations and changes. Review cases show original evidence and distinguish provider-authenticated facts from a user's confirmation. No action directly edits bank facts or a paid total.

Verify at least:

1. An exact accepted receipt automatically pays one eligible request. Leading zeros in VS compare correctly; missing VS does not match zero.
2. Tenant, bank connection, currency, lifecycle, cancellation, and request-status mismatches prevent automatic allocation. Suggestions do not leak foreign data.
3. A receipt arriving before its request is matched after request creation. Installation backfill and worker restarts preserve one allocation and one change event.
4. Partial receipts, top-ups, and overpayments require manual allocation and calculate all amounts and overdue state correctly. Splitting a receipt is rejected.
5. Concurrent workers, manual allocation, request cancellation, and review changes cannot count a receipt twice or leave cancelled requests allocated.
6. Revoking an allocation recalculates the request, preserves history, and blocks automatic rematching. Ignore, restore, and explicit enable follow their separate rules.
7. Duplicate review handles both actual repeated notifications and separate transfers with the same amount/VS. Incompatible proven bank references cannot be overridden as duplicates or distinct copies.
8. A review approval records manual confirmation without rewriting authentication evidence. Incomplete, contradictory, or wrong-account candidates cannot be approved. Closed duplicate cases with other unresolved issues remain ineligible.
9. A newly disputed or review-required allocated transaction loses its allocation atomically, even for inactive owners. Resolution requires explicit reallocation; unchanged reparsing leaves payment history unchanged.
10. Stale versions reject writes. Idempotent retries return one committed decision/allocation/change; reusing a key with different input conflicts. Overflow rolls back the whole operation.
11. Every change in paid amount, including a second partial receipt and a correction, has one ordered durable change record. No outgoing HTTP webhook is sent.
12. Existing request creation replay and QR snapshots remain intact. Partially paid and settled requests cannot obtain an old full-amount QR as new request-specific instructions.
13. Tenant keys, parent management keys, browser sessions, antiforgery, and sibling isolation work for every new endpoint. Fixtures can exercise the flow locally but cannot pay live requests.
14. A controlled end-to-end test creates a request, receives a real supported notification, matches it, and reads the resulting paid state and evidence. Record live verification separately from fixtures; unavailable bank/provider access leaves that check pending.

## 10. Completion boundary

Feature 009 is complete when Pairrot can calculate payment results from actual allocations, explain every automatic decision, and handle manual corrections without losing history or counting receipts twice. [Feature 010](./010-payment-status-webhooks.md) configures and delivers signed payment-status webhooks to NaLekci from the durable change records.

Remaining-balance QR generation, automatic partial matching, refunds, splitting receipts, arbitrary transaction entry, reminders, campaigns, and a production frontend remain outside this feature.
