# Feature 007: bank messages to incoming transactions

## Goal and scope

Turn a stored incoming bank email into a normalized transaction that a later matching feature can compare with an expected payment.

**The first supported bank is Česká spořitelna.** This is the selected implementation target. Support is limited to notification formats covered by verified samples; it is not a promise to understand every email from the bank.

Use messages and persisted source content from [Feature 006](./006-inbound-email-reception.md). Store incoming transactions without changing the states of [Feature 005 payment requests](./005-expected-payments.md). Matching, allocations, manual payment corrections, and outgoing payment-status webhooks remain separate work.

Parsers consume Feature 006's common message snapshot and normalized authentication evidence. They do not reference Resend DTOs, APIs, webhook headers, or provider names. The same bank profile must work when equivalent trusted source data later arrives through an SMTP server or another email service.

## 1. What this feature must distinguish

| Record | Meaning |
| --- | --- |
| Inbound message | The email and provider evidence saved by Feature 006. |
| Parse run | One attempt by a specific parser version to interpret that message. Includes its outcome and reasons. |
| Transaction | The bank movement described by an accepted message. Several messages may describe the same movement. |
| Transaction source | A link between a transaction and a message supporting it. |

Receiving a message, recognizing a template, and trusting a bank notification are separate decisions. All are visible in the result. Neither a credible-looking subject nor a successful webhook signature is enough to declare a payment received.

## 2. Česká spořitelna profile and required samples

Create a parser profile named `ceska_sporitelna`, selected for a saved Czech account with bank code `0800`. Version the parser independently from the profile. The user cannot upload parser code or select arbitrary extraction rules through tenant configuration.

Before implementing the production parser, obtain actual notification samples and define the profile's supported variants:

- Incoming, booked bank transfer with amount, currency, account information, date, VS, and available sender details.
- Incoming transfer without VS and with optional fields omitted.
- Debit, account-balance summary, pending movement, reversal/correction, and unrelated messages where available.
- Plain text and HTML representations actually sent by the bank; note which ones are supported.
- Decimal and date formats, message language, account masking, and the scope and stability of any bank transaction identifier.
- Actual sending address/domain evidence and provider-computed authentication results for direct bank delivery.

Keep sanitized fixtures and expected extraction results in the repository. Preserve the structural features being tested while removing unnecessary personal data. Do not invent sender domains, subject templates, transaction identifiers, or date interpretations and label them as verified bank behavior.

If samples are not yet available, the parser interface, persistence, worker, and tests for generic rules can be implemented. Real Česká spořitelna support remains incomplete until sample-based extraction and source checks are verified. Earlier QR scanning tests in George establish QR compatibility, not bank-email format support.

## 3. Processing flow

1. A worker finds a routed message with available content and no unresolved intake hold.
2. It selects a supported bank profile from the saved destination account.
3. It creates a parse run recording the source digest, profile, parser version, and processing generation.
4. It assesses source authenticity using trusted provider evidence and the bank profile.
5. It recognizes the supported notification template and extracts structured fields from saved source content.
6. It validates direction, booking state, destination account evidence, amount, currency, dates, and identifiers.
7. It resolves message replay and transaction-identity duplicates.
8. In one database transaction, it saves the parse outcome, any new transaction/source link, and job completion.

Use the durable job and lease mechanism from Feature 006. A restart or two workers racing on one message must not produce two transactions. No external provider call is needed for parsing an already downloaded message.

When Feature 007 is installed, schedule previously stored eligible messages as well as newly retrieved ones. This must work regardless of whether a payment request already exists.

## 4. Bank origin and account checks

Define the profile's expected sender identities from actual direct-delivery samples. Require an allowed bank sender and provider-computed authentication evidence that supports the claimed origin. The initial conservative policy requires a passing DMARC result and a corresponding allowed From identity; weaker, missing, or indeterminate evidence requires review. Document any later bank-specific policy change explicitly.

Use the normalized receiving-system evidence and its provenance saved by Feature 006, not an `Authentication-Results` line supplied inside the email. Resend's adapter maps its provider-computed `authentication` results into that contract; a future SMTP adapter must establish an equally trusted receiving-server source. Missing evidence remains unknown and requires review, regardless of provider. [Resend evidence mapping reference](https://resend.com/docs/api-reference/emails/retrieve-received-email).

- Personal forwarded emails, copied text, or an official bank name in the display name do not establish bank origin.
- Local fixture messages retain a `fixture` source marker. They can demonstrate extraction in a development database but must not become live, verified bank evidence.
- Test-environment messages remain test data even when delivered by a real service. Provider kind, test/live environment, and source authenticity are independent properties.
- A failed origin check creates a review outcome, not an incoming transaction eligible for automatic matching.
- The parser cannot reassign the tenant or bank connection chosen by intake.
- Any full destination account found in the notification must agree with the saved connection. A mismatch requires review.
- If the bank masks the account, the profile must state which evidence is sufficient to identify the destination. Do not guess from an ambiguous suffix or pick another tenant's account. Missing or insufficient evidence requires review.

The first bank profile must be tested against both allowed and spoofed sender cases. A parser that extracts the right amount from arbitrary user mail is not sufficient for completion.

## 5. Extraction and normalization

Initially support one booked incoming transfer per notification. Multi-movement summaries, attachments, and statements are unsupported and must not be partially converted by guessing.

| Normalized field | Rule |
| --- | --- |
| `id`, `tenant_id`, `bank_connection_id` | Pairrot assigns ownership from the routed message. |
| `amount` | Positive integer minor units, obtained with exact decimal parsing. Currency precision errors are rejected; never round or use binary floating point. |
| `currency` | Explicit, recognized `CZK`. An absent currency is accepted only if the verified bank template unambiguously supplies it elsewhere. No silent default from the expected payment. |
| `variable_symbol` | Optional validated string of digits; preserve source form. Invalid VS is a review reason, not a silently empty value. |
| `variable_symbol_key` | Same leading-zero comparison rule as Feature 005. Missing VS stays `null`; it is not equivalent to an all-zero symbol. |
| `bank_transaction_id` | Optional stable reference when the verified bank format provides one. Do not substitute an email ID or VS. |
| `sender_account`, `sender_name`, `message` | Optional source values. Normalize accounts only when enough information is present; preserve raw extraction as evidence. |
| `booked_on` | Required bank booking date. Keep a date-only value if that is all the source provides. |
| `occurred_at` | Optional timestamp with an offset, when supported by the source and profile. Do not invent midnight for a date-only notification. |
| `received_at` | Provider reception time, separate from bank booking time and Pairrot processing time. |
| `bank_profile`, `parser_version` | Profile and version that first created the normalized transaction. |
| `ingestion_status` | `accepted`, `review_required`, or `disputed`; separate from future payment-allocation status. |

Incoming amounts use a positive signed 64-bit minor-unit value. Do not impose the QR-generation upper limit on bank receipts: a valid incoming movement may exceed a single generated request's amount. Guard overflow and unsupported precision.

Parse the transaction amount from the correct labeled field, not from the first number or account balance. Debit or pending notices do not create incoming transactions. Reversals and corrections require review; do not implement them as another positive incoming payment.

Prefer the bank profile's documented source part. If text and HTML disagree on payment-critical fields, return review rather than choosing whichever produces a match. HTML extraction is inert: no scripts, external resources, or link following. Preserve the source snapshot unchanged and record what was extracted from it.

## 6. Parse outcomes

| Outcome | Meaning and effect |
| --- | --- |
| `parsed` | Supported notification yielded a transaction and source link. The transaction may still need duplicate review. |
| `duplicate` | The message supports an already known transaction with the same reliable bank identity and compatible facts; add only its source link. |
| `not_payment` | Recognized message is not a booked incoming transfer, for example a debit or balance notice. No transaction. |
| `unsupported` | Bank/profile or message template is not supported. No transaction and no automatic retry loop. |
| `review_required` | Untrusted source, ambiguous values, missing critical data, account mismatch, or a correction notice. Preserve extracted candidates and reasons, but do not produce an accepted transaction. |
| `failed` | Unexpected processing failure. Preserve a safe error and apply bounded job retries. |

Each run stores a stable reason code, readable explanation, extraction candidate where useful, and timestamps. Queue/running/retry state belongs to the job; parse outcomes describe completed attempts.

Candidate data under review is not another list of confirmed bank receipts. API and UI distinguish a parsed candidate from an accepted transaction.

## 7. Duplicate transactions and safe reprocessing

### Same message

One inbound message can support at most one transaction in the initial one-movement-per-message scope. Replaying a webhook or parsing the same source again must not create another movement.

### Different messages about the same movement

- Where the bank profile provides a proven stable transaction reference, enforce uniqueness by bank connection, profile, and reference.
- If a new message has that reference and compatible payment facts, link it to the same transaction.
- If the same reference has different critical facts, record a conflict, mark the existing transaction `disputed`, and create no replacement movement.
- A missing bank reference does not justify collapsing messages by amount and VS. Distinct real transfers may have identical amounts, dates, and symbols.
- Without a reliable reference, create a distinct normalized record for each independently supported message. If its profile-defined comparison fingerprint resembles an existing movement, mark the new record and implicated existing records `review_required`. Preserve all evidence for later resolution; do not count an ambiguous record as accepted money.

Comparison fingerprints are review hints, not unique transaction identities. Include only documented fields and explain their limitations in the bank profile. Tests must distinguish transport duplication from two separate payments with the same amount and symbol.

### Reprocessing after a parser fix

- Keep immutable parse runs and source snapshots. A new run records the new parser version and links to the same message.
- If extraction agrees with the existing transaction, do not add another transaction or silently change its ID.
- If critical data changes, or a previously accepted payment is now recognized as untrusted/non-payment, flag the existing transaction as disputed and preserve the proposed correction in the new run. Do not overwrite or delete history.
- Payment-request states are not changed by this feature, including during reprocessing. Later matching must refuse disputed/review-required records and explicitly handle corrections to any existing allocations.

No manual “trust this source”, merge-transactions, or mark-paid endpoint is introduced here. Review resolution belongs with the later matching and correction feature.

## 8. API and local SPA

| Current-tenant route | Behavior |
| --- | --- |
| `GET /v1/tenant/transactions` | Paginated normalized transactions, filterable by connection, booking date, amount, VS, and ingestion status. |
| `GET /v1/tenant/transactions/{id}` | Payment fields, ingestion status, reasons, and permitted source-message links. |
| `GET /v1/tenant/inbound-messages/{id}/parse-runs` | History of parsing attempts, versions, candidates, and outcomes. |
| `POST /v1/tenant/inbound-messages/{id}/reprocess` | Queue a new parse generation for available eligible source; return `202` with the job ID. |

Mirror these operations under `/v1/subtenants/{subtenantId}` using existing direct-child management permissions. Tenant keys and administrator sessions access current-tenant routes; parent management keys and permitted parent sessions access child routes. Foreign records and source links return `404`.

A repeated reprocess call while a job is queued or running returns its ID instead of queuing another job. Reprocess uses the currently installed bank profile; clients cannot inject parser code, a trusted-source flag, extracted amounts, or another owner. Missing content or an unresolved intake hold returns `409` with its reason. Browser writes require antiforgery and expected-tenant checks.

For archived connections and inactive tenant hierarchies, keep history readable to the authorized parent but hold new parsing and user-triggered reprocessing. An operator may queue eligible messages again after reactivation; a status change alone must not silently rewrite old outcomes.

The SPA shows:

- Whether source content is available, separately from the latest parse outcome.
- Recognized bank, parser version, source-trust result, extracted fields, and reasons for review or rejection.
- Normalized incoming transactions and their supporting messages.
- A reprocess action and its job state.

Show “bank transaction recorded” rather than “reservation paid”. Do not display all parsed candidates as a received-money total.

## 9. Logical schema

| Table/change | Important fields and constraints |
| --- | --- |
| `message_parse_runs` | Message, tenant/connection, job generation, source digest, parser profile/version, trust decision/evidence summary, outcome, reason, extracted candidate, start/end times. Unique message/generation. |
| `incoming_transactions` | Normalized fields above, creation time, ingestion status/reasons, optional bank reference, comparison fingerprint. Partial unique index on connection/profile/non-null bank reference. |
| `transaction_sources` | Transaction, message, and establishing parse run. Message is unique in the one-movement-per-message scope. Many messages may support one transaction. |
| Inbound message projection | Latest completed parse-run ID and parsed time, separate from content retrieval and routing states. |

Use tenant/connection-consistent relationships between messages, runs, transactions, and source links. Prevent cross-tenant linkage at the database layer as well as in API checks. Candidate extraction can be JSON; normalized monetary, identity, and date fields belong in typed columns.

Persist a run's final outcome, transaction/source changes, affected review states, and job completion atomically. Record manual reprocess requests in the tenant audit history; automated runs retain their own detailed processing history and named worker identity.

No allocation table, paid-amount update, or outgoing customer webhook is added by this migration.

## 10. Acceptance criteria

1. Verified direct-delivery Česká spořitelna samples for the supported variants produce the expected account, direction, amount, currency, date, VS, and available sender fields.
2. Incoming notices without VS still produce a transaction when all required bank and trust evidence is sufficient. Missing amount/currency or ambiguous destination evidence produces review.
3. Spoofed senders, forged authentication headers, personal forwards, and insufficient provider evidence cannot create accepted transactions.
4. Debit, balance, pending-payment, correction, and unsupported notices are classified without accidentally creating positive incoming payments.
5. Text/HTML conflicts, unknown template changes, date ambiguity, invalid symbols, and amount overflow/precision errors are visible and do not silently fabricate values.
6. Duplicate webhook delivery, repeated parsing, concurrency, and worker restart preserve one transaction per actual recognized identity.
7. Two messages with the same proven bank reference link to one movement. Different proven references remain separate even when amount and VS match. Uncertain similarity is flagged, not silently merged.
8. A parser upgrade can reprocess old saved content without Resend access. Unchanged facts keep the transaction ID; changed critical facts produce a dispute and preserve history.
9. Source links, transaction lists, parse history, and reprocess operations enforce tenant and parent permissions.
10. Fixture-generated records remain identifiable as test evidence and cannot masquerade as verified live bank receipts.
11. Messages received before Feature 007 or before a payment request are processed when eligible. No expected-payment state changes and no customer webhook is sent.
12. Record a controlled end-to-end result for a real direct Česká spořitelna notification through Feature 006, including its source-authentication evidence and extraction result. Without this, report live bank support as unverified even if fixture tests pass.
13. Equivalent common snapshots from fake Resend and SMTP-style adapters produce the same extraction and trust decisions. No parser logic branches on provider kind; weaker evidence never gains trust just by changing the adapter name.

## 11. Completion boundary

The selected bank is Česká spořitelna; the remaining external input is its actual notification samples and authentication evidence. This is needed to finish the concrete bank profile, not a reason to invent one.

After Feature 007, Pairrot has expected payments and separately recorded incoming transactions. [Feature 008](./008-forwarded-bank-notifications.md) adds forwarded notifications and protection against foreign mail; a later feature matches them and handles manual resolution. Payment-status delivery to NaLekci follows in its own feature. Campaigns remain disabled.
