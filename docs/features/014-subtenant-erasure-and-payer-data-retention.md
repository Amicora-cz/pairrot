# Feature 014: subtenant erasure and payer-data retention

Status: implemented (see [Implementation notes](#implementation-notes)). Builds on [Feature 013](./013-bank-account-identity-and-reuse.md).

## Goal and scope

`DELETE /v1/subtenants/{id}` revokes keys and marks the tenant deleted, and keeps everything else: the tenant's name and contact e-mail, bank-connection labels, account numbers, forwarding mailboxes, every stored bank e-mail with the payers' names and accounts, every transaction's sender fields, free-text reasons, and audit details. Nothing in Pairrot ever deletes a bank message, a parse run, or a transaction.

Two groups of people are affected. The tenant (an instructor, a sole trader whose account number is personal data) is promised erasure by the integrating application. The payers named in the notifications never had any relationship with Pairrot and are kept indefinitely.

Pairrot is not an accounting system (long-term specification, section 6). The evidence of a payment for accounting purposes is the payee's bank statement and the integrating application's records. Pairrot keeps financial facts and internal identifiers needed to reconcile its history. Caller-supplied identifiers and diagnostic strings can name people and are included in erasure. The physical bank-account identity is an explicit retained exception, not a claim of anonymization.

This feature adds an explicit erasure of a deleted subtenant and a retention limit for payer data on every tenant. It does not change deletion itself, and it does not erase root tenants over HTTP.

## 1. Erasure of a deleted subtenant

- Only a `deleted` subtenant can be erased. `Erasure:MinimumAgeAfterDeletion` (default `7.00:00:00`) must have passed since `deleted_at`, so a deletion by mistake can still be inspected; `0` disables the wait.
- Before requesting erasure, the parent must complete any reconciliation that needs the original `external_id` or webhook history. Erasure tombstones those references and removes replayable payloads, so the parent can no longer find a historical request by its original reservation reference in Pairrot. The default seven-day wait provides an opportunity, not a guaranteed reconciliation window: it may be configured to zero. Pairrot cannot verify completion of the parent's reconciliation. NaLekci integration task #400 must enforce this ordering before calling `erase`.
- `POST /v1/subtenants/{id}/erase` with `Idempotency-Key` and a `reason`, by the management key or an authorized parent administrator session. The operator CLI has `tenants erase <tenant> --reason <text>` for any tenant, root tenants included.
- Erasure runs as a durable background job (Feature 006 jobs and leases), in batches, so a tenant with years of mail does not hold one request or one transaction open. The command returns `202` with the job ID; `GET /v1/subtenants/{id}` shows `erasure: { state, requested_at, completed_at, requested_by }` with `state` `queued`, `running`, or `done`.
- Requesting erasure atomically establishes a permanent collection barrier and abandons pending deliveries of events owned by this tenant, including deliveries to the parent's endpoint. The tenant's own endpoint is disabled and its signing keys lose their secrets. Event payloads are purged as described below; financial history remains.
- Repeating the command returns the current state with `Idempotent-Replayed: true`. Erasing an already erased tenant is a no-op.
- Erasure writes one `tenant.erased` audit event with counts per record type, and nothing else about what was removed.

## 2. What is erased and what stays

| Record | Erased | Kept |
| --- | --- | --- |
| `tenants` | `name` → `erased`, `contact_email` → `null`, `external_id` → unique `erased:<id>` tombstone, `erased_at` set | internal ID, status, timestamps |
| `tenant_memberships` | removed | user accounts are operators' accounts and are not touched |
| `bank_connections` | `label` → `null`; `account_number` and `iban` snapshots → masked (`…/0800`, `CZ…`) | `bank_account_id`, status, dates, the inbound address (a random routing key that must stay reserved) |
| `bank_accounts` | no sender/content fields may be introduced here | Normalized IBAN/account number keeps the symbol namespace intact (Feature 013); retained `incoming_transactions` below hold bank-reference uniqueness and comparison facts without a separate movement registry. These are explicitly retained, potentially identifying data. Full account identity is accessible only to the operator or a tenant with its own non-erased registration, never through an erased connection's response. |
| `forwarding_sources` | address → `null`, `erased` flag | state, dates |
| `payment_requests` | `message`, `description`, `cancellation_reason` → `null`; `external_id` → unique `erased:<id>` tombstone; `account_number`/`iban` snapshots masked; `qr_payload` → `null`, `qr_erased_at` set | amount, currency, symbol and key, status, `paid_amount`, `change_sequence`, fingerprint, timestamps, versions |
| `payment_request_changes` | caller references and free text, including values embedded in snapshots → purged/tombstoned | internal IDs, amounts, statuses, sequence, timestamps, technical actor |
| `webhook_events`, deliveries and attempts | event body → `null`, `payload_erased_at` set; response excerpts and free-text errors → `null`; abandon unsent deliveries with `tenant_erased` at every destination | event identity/type, owner, subject, sequence, body digest, attempt statuses and times; never a replacement payload under an existing event ID |
| Tenant's webhook endpoint and destination snapshots | URL and any caller-supplied labels → purged; encrypted signing secrets → purged | technical IDs, disabled state and timestamps; a parent's endpoint configuration stays |
| `inbound_messages` | subject, header From, envelope sender, sender domain, non-routing recipients, text, HTML, headers, `transport_metadata`, raw provider content, attachment metadata, raw authentication, free-text fetch errors → purged; `content_state` → `erased` | opaque provider IDs, instance, received time, reserved Pairrot routing address, normalized authentication result codes and trusted adapter origin, routing/hold/sender-class/parse codes |
| `inbound_delivery_receipts` | `raw_payload` → `null`, including receipts for duplicate deliveries | opaque delivery/message IDs, digest, outcome, timestamps |
| `message_parse_runs` | `candidate`, `trust_evidence`, `reason_message` and free-text entries in `reasons` → purged | outcome, reason codes, template, profile/parser versions, digest, timestamps |
| `incoming_transactions` | `sender_account`, `sender_account_raw`, `sender_name`, `message` and free-text ingestion reasons → `null` | amount, currency, symbol, bank reference, booking/received times, status and reason codes, handling, origin path, versions |
| `payment_allocations`, `payment_review_decisions`, `incoming_transactions.handling_reason` | free-text `reason` → `erased` | codes, actors, versions, timestamps |
| `manual_command_receipts` and other idempotency records | response bodies, raw caller-supplied keys/references → purged | keyed digests of keys, action, fingerprint, status and internal IDs, sufficient to prevent replay from recreating data |
| Background jobs, review proposals, erasure requests and forwarding verification challenges | copied content, free-text errors/reasons, caller labels and verification secrets → purged | internal targets, operation/state, reason codes, counts, timestamps |
| `tenant_audit_events` | `details` → `{"erased": true}` on events targeting the tenant | action, actor, timestamps |
| Acting-user attribution ([Feature 018](./018-acting-user-attribution.md)) | the acting user ID and label on the tenant's records → `null` | |

Masking keeps the bank code and the last two digits so an operator can still tell two connections apart.

The implementation must inventory every persisted copy, including JSON, job errors, audit rows owned by the parent but targeting the child, and response snapshots. Diagnostic text is not safe merely because it is bounded: today's webhook `SafeExcerpt` truncates/escapes but does not remove personal data. Retained technical actors identify the authenticating key/operator; acting-user values are erased. The erasure command's own free-text reason is not retained after completion.

Caller-supplied `external_id` is arbitrary text, so this feature overrides earlier byte-preservation promises for those fields and event bodies. Integrators should use opaque references, but erasure must also handle historical e-mail addresses or names in them. A new schema permits nullable erased payloads and introduces tombstones without weakening financial consistency checks.

## 3. Behavior after erasure

- Authorized parent/operator reads return the record with `erased_at` and fields as `null` or placeholders. Existing authorization rules still apply: siblings receive `404`, and revoked tenant keys do not regain access. `GET .../inbound-messages/{id}/source` returns `409 content_erased`; so does `reprocess`.
- Business mutations on the tenant are refused as today for a deleted tenant; replay of a request creation for an erased tenant is `409 tenant_erased`, not a re-creation. The authorized erase command itself remains idempotent and maintenance may finish purging.
- Webhook replay/retry of an erased payload is `409 event_payload_erased`, including at the parent's endpoint. Event listings return metadata and the erasure marker, never a reconstructed body. Already delivered copies belong to the receiver's retention process and cannot be recalled by Pairrot.
- `payments check` passes unchanged: totals, allocations, and sequences are intact.
- The SPA shows an erased tenant, connection, request, message, and transaction as erased, and the CLI `inbound show` says `content erased` rather than reporting a retrieval failure.

### Collection barrier and concurrent work

The erasure request increments a persisted tenant data epoch while holding the same tenant barrier lock used by data-producing commits. From `queued` onwards, intake, duplicate-delivery receipts, discovery, fetch, parse, manual reprocessing, and event creation may persist only the minimized tombstone metadata allowed above. They never retrieve new content for that tenant. Existing fetch/parse jobs are cancelled or completed as `content_erased`; purging itself remains allowed.

Workers capture the epoch before external I/O and recheck it under the barrier lock before committing content, candidates, errors, or events. A stale result is discarded, not rescheduled. New deliveries and provider retries to a reserved erased address are acknowledged and deduplicated using minimal IDs/digests without storing the payload. A bounded final pass verifies no prohibited fields remain before marking `done`.

Webhook dispatch rechecks the barrier before sending. Erasure cancels outstanding leases and waits for already started sends to finish or time out before purging their bodies and recording completion. A request already sent cannot be recalled; late results may record status/timing only, never response text. This limitation is reported by the erasure job rather than promising recall.

## 4. Payer-data retention on every tenant

Payer data is identified by what the notification said about the counterparty and by the stored e-mail itself. Independently of erasure, a daily retention job purges it after `Retention:PayerData` (default `730.00:00:00`, two years, counted from `received_at`):

- `inbound_messages`: the same fields as in section 2 → purged, `content_state` → `expired`.
- `inbound_delivery_receipts.raw_payload`, parse candidates/trust evidence, and diagnostic text copied from that message in parse runs, review proposals, transaction ingestion reasons, and job errors → purged as in section 2.
- `incoming_transactions`: the four sender fields → `null`.

It purges regardless of allocation or review state; unresolved cases retain financial facts and explicitly show that source evidence expired. It preserves financial amounts, symbols, statuses and allocations, and never extends Feature 008's shorter retention (foreign metadata 30 days, confirmations 7 days). The job records counts per instance. `expired` and `erased` are terminal content states; `erased` takes precedence. Fetch, retry-fetch, and reprocess reject both (`content_expired` or `content_erased`). Intake and worker commits check the received-time cutoff as well as the tenant barrier, so delayed deliveries and an in-flight fetch cannot repopulate expired content. Retention does not erase active tenants' independently supplied business references; full tenant erasure does.

The root tenant and the integrating application's own data follow the same rule.

These operations purge the live Pairrot database. Provider-held mail, exported copies, backups and log sinks have separate, documented retention policies; completion does not assert their immediate deletion. Before production rollout, document those periods and a restore procedure that reapplies completed erasure barriers and retention before restored data becomes available. Feature 018 keeps raw acting-user values out of operational logs.

## 5. Configuration, API, and CLI

| Setting | Default | Meaning |
| --- | --- | --- |
| `Erasure:MinimumAgeAfterDeletion` | `7.00:00:00` | Wait after deletion before erasure is accepted. |
| `Erasure:BatchSize` | `500` | Records per erasure batch. |
| `Retention:PayerData` | `730.00:00:00` | Age after which payer data is purged. |
| `Retention:Enabled` | `true` | |

| Method and path | Behavior |
| --- | --- |
| `POST /v1/subtenants/{id}/erase` | Queue erasure; `202` with the job; `409 tenant_not_deleted`, `409 erasure_too_early`. |
| `GET /v1/subtenants/{id}` | Includes `erasure` state once requested. |
| CLI `tenants erase <tenant> --reason <text>` | Any tenant, operator only; same job. |
| CLI `tenants erasure-status [<tenant>]` | Queued, running, and done erasures with counts. |
| CLI `retention run` | Runs one retention pass now and prints counts. |

## 6. Acceptance criteria

1. A full database dump after erasure contains none of the seeded tenant/payer names, e-mails, labels, forwarding mailboxes or free-text reasons, including values seeded in `external_id`, webhook response excerpts, transport metadata, duplicate receipts, parse errors and parent audit rows. Explicit exceptions are account identifiers in `bank_accounts` and the documented retained financial facts; erased APIs never reveal the full account through those references.
2. Financial amounts, symbols, statuses, internal IDs, sequences and allocations are unchanged and `payments check` passes. Caller references are tombstoned, event bodies are absent, and replay is refused without rebuilding or renumbering an event.
3. Erasure is idempotent and resumable: a worker killed mid-way leaves a job that another worker finishes, and the final counts match a single uninterrupted run.
4. Only a deleted subtenant past the minimum age can be erased; parent authority, sibling `404`, and browser checks apply; business mutations are refused afterwards with `tenant_erased`, while repeating erase remains a no-op.
5. The account of an erased tenant can be registered by a new tenant (Feature 013) and the symbol namespace and bank-reference uniqueness still hold.
6. Retention purges payer data at the configured age for live, test, and fixture messages, never earlier, never anything else, and reports counts. A message younger than the limit is untouched.
7. Erased and expired content is reported by the API, the SPA, and the CLI as erased or expired, not as a failure.
8. Pending deliveries of the tenant's events to both owner and parent are abandoned, and their replay is refused; unrelated tenants' deliveries and the parent's endpoint remain usable. An already started send completes/times out before erasure reports done, and its response cannot repopulate erased text.
9. Pause fetch/parse workers before commit, queue erasure, then release them: no content or candidates reappear. Repeat with retention expiry, duplicate provider webhooks, late discovery, and new mail sent after erasure completed. All retain only permitted tombstones and financial facts.

## 7. Not included

Erasure of payers on request (a payer asking Pairrot directly), export of a tenant's data, erasure of root tenants over HTTP, and any change to deletion itself.

## Implementation notes

- **Barrier.** `tenants.erasure_requested_at` is the collection barrier and, being permanent, also serves as the data epoch: no counter is needed. Data-producing commits (intake, content retrieval, parsing, webhook event creation, webhook results) take the tenant's shared advisory lock in their own transaction and then check it; the erase request sets it under the exclusive lock. Content retrieval and parsing also lock the message row before committing, which orders them against retention.
- **Erasure job.** `erase_tenant` (target: the tenant) runs a fixed list of purge steps, each changing at most `Erasure:BatchSize` rows that still hold personal data. Every batch is one transaction that also locks the job row and checks the lease, and adds its row count to `tenant_erasures.counts`; a resumed job therefore reports the counts of an uninterrupted run. A pass that changes nothing is the final verification. While a webhook request of the tenant is in flight, the job waits (retry at the lease expiry) and purges its result afterwards.
- **Retained, as allowed:** internal IDs, amounts, currencies, symbols, bank references, statuses, sequences, versions, timestamps, technical actors, reason codes, digests (`content_sha256`, `body_sha256`, receipt digests), opaque provider IDs and content references, the reserved routing address, and normalized authentication results. Inbound addresses keep their address; erased messages keep only it among their recipients.
- **Placeholders.** Required free text becomes `erased` (allocation, decision, and handling reasons, review case texts); caller references become `erased:<record id>` (`payment_request_changes` use the request's ID). Command receipts keep `sha256:<hex>` of the key and no response; a retry with that key is `409 tenant_erased`. The masks are computed in SQL; the IBAN mask has the database pattern `^CZ…[0-9]{4}…[0-9]{2}$`. Payment requests use one `erased_at` instead of a separate `qr_erased_at`.
- **Operator CLI.** `tenants erase` deletes a tenant that is not deleted yet (refused while it has subtenants that are not deleted) and skips the minimum age. The erasure's single audit event `tenant.erased` has the actor `pairrot:erasure`.
- **Retention** marks messages `expired` with the same field purge, then purges receipts, parse runs, review texts quoting them, and job errors of those messages, and the sender fields of transactions received before the cutoff. Review texts are set to `expired`. Retention is skipped in intake and workers when `Retention:Enabled` is false.
- **Acting-user attribution** (Feature 018) does not exist yet; its erasure is part of Feature 018.
