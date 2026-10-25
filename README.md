# Pairrot

Pairrot matches incoming bank payments to expected payments and reports the results to its customers. It runs as a single ASP.NET Core application that serves an API and also runs background processing. Its first customer is [NaLekci](https://nalekci.cz).

Implemented so far:

- **Feature 1: tenants, subtenants, and API access** ([spec](docs/features/001-tenants.md))
- **Feature 2: user login, sessions, and API key management** ([spec](docs/features/002-user-login-and-sessions.md))
- **Feature 3: bank configuration and an inbound email address** ([spec](docs/features/003-bank-configuration-and-inbound-address.md)), with a local stub instead of Resend
- **Feature 4: payment QR generation** ([spec](docs/features/004-payment-qr-generation.md)): Czech QR Platba (SPAYD) payloads and PNG codes for saved accounts
- **Feature 5: expected payments** ([spec](docs/features/005-expected-payments.md)): saved payment requests with a reservation reference, frozen payment instructions, edits, and cancellation
- **Feature 6: inbound email reception** ([spec](docs/features/006-inbound-email-reception.md)): a provider-independent intake with Resend as the first live adapter, verified webhooks, a startup and hourly recovery check for missed webhooks, routing to bank connections, and stored source content retrieved by a background worker. Live reception through a real Resend account is not verified yet (see [below](#inbound-email))
- **Feature 7: bank messages to incoming transactions** ([spec](docs/features/007-bank-message-parsing.md)): parse jobs, source trust, normalization, duplicate and reprocessing rules, transactions with their supporting messages, the API, the SPA, and the CLI. The Česká spořitelna profile is built from a real notification received end to end (see [below](#bank-message-parsing))
- **Feature 8: forwarded bank notifications** ([spec](docs/features/008-forwarded-bank-notifications.md)): notifications forwarded from the tenant's registered mailbox are valid. Foreign mail is never retrieved or stored, and an address that receives too much of it is paused. Real Gmail and Seznam forwarding samples are not recorded yet (see [below](#forwarded-notifications))

- **Feature 9: payment matching and manual review** ([spec](docs/features/009-payment-matching-and-review.md)): exact automatic matching, manual allocations and corrections, paid/partially paid/overpaid totals, review of duplicates, disputes, and unverified notifications, idempotent manual commands, and a durable payment-change journal (see [below](#payment-matching-and-review)). A live end-to-end match against a real Česká spořitelna notification is not recorded yet

- **Feature 10: payment-status webhooks** ([spec](docs/features/010-payment-status-webhooks.md)): signed `payment_request.updated` events for the owner and a parent subscribed to its direct children, durable delivery history, signing-key rotation, test probes, and manual retry, replay, and abandonment (see [below](#payment-status-webhooks)). The receiver contract and a .NET example are in [docs/webhooks-receiver.md](docs/webhooks-receiver.md). NaLekci's own receiver is a separate integration and not verified yet
- **Feature 11: webhook delivery retries** ([spec](docs/features/011-webhook-delivery-retries.md)): four attempts per delivery, the retries 5 seconds, 60 seconds, and 1 hour after each failure, restart-safe and visible after the last failure

- **Feature 12: automatic variable symbol generation** ([spec](docs/features/012-automatic-variable-symbol-generation.md)): payment-request creation without `variable_symbol` gets the next unused symbol of its bank account, allocated under a per-account lock in PostgreSQL and replayed on retries; caller-supplied symbols work as before (see [below](#expected-payments))
- **Feature 13: bank account identity, release, and reuse** ([spec](docs/features/013-bank-account-identity-and-reuse.md)): a physical account is its own record; only active registrations are exclusive, so archiving or deleting a tenant releases the account, and an archived connection can be restored. Variable symbols, bank references, and lookalike comparison are per account; a tenant's old requests stay payable through its new registration, and another tenant's history is never visible (see [below](#bank-connections))

- **Feature 14: subtenant erasure and payer-data retention** ([spec](docs/features/014-subtenant-erasure-and-payer-data-retention.md)): `POST /v1/subtenants/{id}/erase` purges a deleted child's personal data in a background job (names, contacts, labels, mailboxes, stored e-mails, payer details, free text, caller references, webhook payloads) while amounts, symbols, statuses, allocations, and internal IDs stay; a permanent collection barrier keeps in-flight and later work from bringing anything back. Payer data of every tenant is purged two years after reception (see [below](#erasure-and-retention))

- **Feature 16: attention events and the parent attention list** ([spec](docs/features/016-attention-events-and-parent-attention-list.md)): `transaction.recorded`/`updated`, `review_case.opened`/`updated`, and `inbound_address.paused`/`resumed` events with per-subject sequences, per-endpoint `event_types` subscriptions (payment changes only by default), and `GET /v1/management/attention` and `/v1/tenant/attention` with a summary: unmatched receipts, open review cases, paused addresses, failed deliveries, and overpaid (and, on request, overdue) requests across the direct children (see [below](#attention-events-and-the-attention-list)). The reception-readiness parts moved to Feature 15

- **Feature 21: local sandbox mode** ([spec](docs/features/021-sandbox-mode.md)): with `Sandbox:Enabled` (Development and Testing only), `POST .../bank-connections/{id}/sandbox/incoming-payments` and `POST .../payment-requests/{id}/sandbox/pay` render a simulated payment as the bank's own notification and hand it to the real intake through the connection's test address, so parsing, matching, review, the payment journal, and test webhooks run exactly as for a real e-mail (see [below](#sandbox-mode)). The `sandbox seed` command and the sandbox badge and forms of the SPA are described in the spec

Proposed, not implemented (specs only, written against NaLekci's integration brief of 2026-10-08):

- [Feature 15: supported banks and reception readiness](docs/features/015-supported-banks-and-reception-readiness.md) (deferred: with one supported bank it is not needed yet)
- [Feature 17: webhook retry budget and endpoint recovery](docs/features/017-webhook-retry-budget-and-endpoint-recovery.md)
- [Feature 18: acting-user attribution](docs/features/018-acting-user-attribution.md)
- [Feature 19: operational health and alerting](docs/features/019-operational-health-and-alerting.md)
- [Feature 20: forwarding source verification and provider samples](docs/features/020-forwarding-source-verification.md)

The target architecture is described in [docs/architektura.md](docs/architektura.md) (in Czech).

## Tech stack

- .NET 10, ASP.NET Core minimal APIs
- EF Core 10 with Npgsql (PostgreSQL), snake_case naming
- ASP.NET Core Identity (password storage), cookie authentication, antiforgery, Data Protection keys in PostgreSQL
- PostgreSQL 17 in Docker, locally (`docker-compose.yml`) and in production (`deploy/docker-compose.yml`)
- Vite + React + TypeScript for the local test SPA (never deployed)
- xUnit, Testcontainers

## Repository layout

```
src/
  Pairrot.Api              HTTP API: API keys and browser sessions, /v1/auth, /v1/tenant, /v1/subtenants, /v1/webhooks
  Pairrot.Core             Domain entities, input rules, API key format, Czech account and IBAN validation,
                           the common inbound delivery contract
  Pairrot.Infrastructure   EF Core DbContext, migrations, services, inbound intake and routing, background jobs;
                           provider adapters in Inbound/<Provider> (Resend)
  Pairrot.Admin            Administrative CLI: migrations, root tenant setup, root keys, users, inbound mail
tests/
  Pairrot.UnitTests
  Pairrot.IntegrationTests API tests against a real PostgreSQL (Testcontainers)
web/                       Local Vite + React test SPA
docs/                      Specifications (Czech) and feature scopes
```

## Getting started

Prerequisites: .NET SDK 10 (see `global.json`), Docker, and Node.js 20+ for the SPA.

```bash
# 1. Start PostgreSQL. It listens on localhost:5433 and keeps its data in a Docker volume.
docker compose up -d

# 2. Restore local tools (dotnet-ef).
dotnet tool restore

# 3. Apply migrations, create the nalekci.cz root tenant and its first administrator.
#    The tenant key and management key are printed once. Store them.
#    The administrator's password is prompted (not echoed, never logged).
dotnet run --project src/Pairrot.Admin -- setup --admin-email you@example.com

# 4. Run the API over HTTPS (https://localhost:7017, also http://localhost:5034).
dotnet run --project src/Pairrot.Api --launch-profile https
```

API-key clients (Feature 1) can use plain HTTP on port 5034. Browser sessions need HTTPS because the session cookie is `Secure`.

To change the database name, credentials, or port, copy `.env.example` to `.env`. If you change them, also update `ConnectionStrings:Pairrot` in `appsettings.Development.json`.

Example requests are in [src/Pairrot.Api/Pairrot.Api.http](src/Pairrot.Api/Pairrot.Api.http).

### Local SPA

```bash
cd web
npm install
dotnet dev-certs https --trust   # once per machine, so the browser trusts localhost
npm run cert                     # exports the ASP.NET Core dev certificate to web/.cert (git-ignored)
npm run dev                      # https://localhost:5173
```

Sign in with the administrator created by `setup` or `users create`. Vite proxies `/v1/...` to `https://localhost:7017`, so the SPA and the API share one origin; set `PAIRROT_API_URL` to proxy elsewhere. The SPA keeps the antiforgery token and one-time key secrets in memory only, never in browser storage.

## Configuration

The database connection is read from `ConnectionStrings:Pairrot` (environment variable: `ConnectionStrings__Pairrot`). Local development values are in `appsettings.Development.json`. To use any other PostgreSQL, set the environment variable; no code changes are needed:

```bash
ConnectionStrings__Pairrot="Host=<host>;Database=pairrot;Username=<user>;Password=<password>;SSL Mode=Require"
```

Do not commit real credentials.

Browser sign-in settings are application configuration in the `Auth` section (for example `Auth__SessionIdleTimeout`):

| Setting | Default | Meaning |
| --- | --- | --- |
| `SessionIdleTimeout` | `00:30:00` | A session ends after this long without user activity. Requests with `X-Pairrot-Background: true` (polling) do not count as activity. |
| `SessionAbsoluteTimeout` | `08:00:00` | A session ends this long after login, even if the user stays active. |
| `LockoutMaxFailedAttempts`, `LockoutDuration` | `5`, `00:05:00` | Temporary account lockout after repeated wrong passwords. |
| `LoginRateLimitPermits`, `LoginRateLimitWindow` | `10`, `00:01:00` | Login requests allowed per client IP address and window; excess requests get `429`. |
| `PasswordMinLength` | `12` | Minimum password length. |

Inbound email is configured in the `InboundEmail` section as a registry of provider instances. Each instance has a stable ID that is stored with its addresses and messages, so never reuse an ID for another provider account. `DefaultInstance` chooses the instance for new addresses; changing it never rewrites existing addresses or redirects queued retrievals. By default only the built-in `stub` instance exists:

| Setting | Default | Meaning |
| --- | --- | --- |
| `DefaultInstance` | `stub` | Instance used for new notification addresses. |
| `MaxContentBytes` | `5242880` | Pairrot's limit for one stored content snapshot, inline or retrieved. |
| `Instances:<id>:Kind` | | Adapter kind: `stub` or `resend`. |
| `Instances:<id>:Environment` | `test` | `test` or `live`. A live instance cannot use a reserved test domain. |
| `Instances:<id>:SourceKind` | `received` | `fixture` marks every message of the instance as test evidence. |
| `Instances:<id>:Domain` | | Domain of generated addresses. The stub requires a reserved test domain (`in.pairrot.test`). |
| `Instances:<id>:Enabled` | `true` | A disabled instance accepts no new mail; its history and queued retrievals stay. |

A `resend` instance also needs these settings, which are server-side only and never stored per tenant or sent to the SPA:

| Setting | Default | Meaning |
| --- | --- | --- |
| `ApiKey` | | Resend API key, used only by the background worker to retrieve received content. |
| `WebhookSecret` | | The webhook endpoint's signing secret (`whsec_...`). |
| `ApiBaseUrl` | `https://api.resend.com/` | |
| `MaxWebhookBytes`, `MaxContentBytes` | 1 MiB, 5 MiB | Limits for a webhook body and a Receiving API response (also each page of the message list). |
| `RequestTimeout` | `00:00:30` | |
| `DiscoveryPageSize` | `100` | Page size of the recovery check's message listing. |
| `HistoryRetention` | not set | How long the Resend account keeps received emails, if known. Recovery from further back is reported as a gap. |

For example, with environment variables:

```bash
InboundEmail__DefaultInstance=resend-main
InboundEmail__Instances__resend-main__Kind=resend
InboundEmail__Instances__resend-main__Environment=live
InboundEmail__Instances__resend-main__Domain=<your receiving domain>
InboundEmail__Instances__resend-main__ApiKey=<re_...>
InboundEmail__Instances__resend-main__WebhookSecret=<whsec_...>
```

Point the Resend `email.received` webhook at `https://<public host>/v1/webhooks/resend/<instance-id>` (`/v1/webhooks/resend` also works while exactly one Resend instance is enabled). Invalid configuration stops the API at startup with every problem listed.

The recovery check runs once when the API starts and then every `InboundEmail:Discovery:Interval` (default `01:00:00`), covering from the last completed boundary minus `InboundEmail:Discovery:Overlap` (default `01:00:00`).

The API process runs a background worker for durable jobs in PostgreSQL. `BackgroundJobs` settings: `Enabled` (`true`), `PollInterval` (`01:00:00`), `LeaseDuration` (`00:05:00`), `MaxAttempts` (`6`), and `RetryDelays` (`00:01:00`, `00:05:00`, `00:30:00`, `02:00:00`, `06:00:00`). The worker does not poll: a saved job wakes it at once (after its transaction commits), and otherwise it sleeps until the next due time or lease expiry, at most `PollInterval`. An idle API therefore leaves the database idle, which lets a serverless database suspend.

Cookie-protection (Data Protection) keys are stored in the `data_protection_keys` table, so sessions survive restarts and work across instances that share the database. The same key ring encrypts webhook signing secrets, so losing it makes deliveries wait as `signing_key_unavailable` until the secret is rotated.

Erasure and retention (Feature 14): `Erasure:MinimumAgeAfterDeletion` (`7.00:00:00`; `0` disables the wait), `Erasure:BatchSize` (`500`), `Retention:PayerData` (`730.00:00:00`), and `Retention:Enabled` (`true`). The retention pass runs at startup and then daily in the API process.

Outgoing webhooks are configured in the `Webhooks` section: `DispatcherEnabled` (`true`), `PollInterval` (`01:00:00`, the longest sleep; saved deliveries and endpoint changes wake the dispatcher, which otherwise sleeps until the next due time), `LeaseDuration` (`00:01:00`), `MaxParallel` (`8`, with at most one request per endpoint in flight), `MaintenanceInterval` (`01:00:00`), and `AllowedLocalDestinations` (empty). The last one lists `host:port` entries, e.g. `localhost:5099`, that may use HTTP and a private address for a local test receiver; the API refuses to start with it outside Development and Testing. Payment matching uses `Matching:SweepInterval` (`01:00:00`) and `Matching:AllowTestEvidence` (`false`, Development and Testing only). `Sandbox:Enabled` (`false`, Development and Testing only; the API and the CLI refuse to start otherwise) turns on sandbox mode and implies `Matching:AllowTestEvidence`.

## API overview

JSON uses snake_case fields. There are two ways to authenticate:

- **API keys** for integrations: `Authorization: Bearer <key>`, format `prt_<key-id>_<secret>`. Pairrot stores only a SHA-256 hash of the secret, so a key's secret is shown only when the key is created.
- **Browser sessions** for people: email and password, then an `HttpOnly` cookie that identifies a server-side session row in PostgreSQL. The session, the user, and the membership are re-checked on every request.

If an `Authorization` header is present, only the key is checked; an invalid key never falls back to the cookie.

| Caller | Endpoints |
| --- | --- |
| Tenant key, or a session with the tenant selected | `GET /v1/tenant`, `GET/PATCH /v1/tenant/configuration`, `GET/POST /v1/tenant/bank-connections`, `GET/PATCH/DELETE /v1/tenant/bank-connections/{id}`, `POST /v1/tenant/bank-connections/{id}/inbound-address`, `POST /v1/tenant/bank-connections/{id}/payment-qr` and `.../payment-qr.png`, `GET/POST /v1/tenant/payment-requests`, `GET/PATCH /v1/tenant/payment-requests/{id}`, `POST .../{id}/cancel`, `GET .../{id}/payment-instructions` and `.../{id}/qr.png`, `GET /v1/tenant/inbound-messages`, `GET .../inbound-messages/{id}`, `GET .../{id}/source`, `POST .../{id}/retry-fetch`, `GET .../{id}/parse-runs`, `POST .../{id}/reprocess`, `GET /v1/tenant/transactions`, `GET /v1/tenant/transactions/{id}`, `GET/POST .../bank-connections/{id}/forwarding-sources`, `DELETE .../forwarding-sources/{sourceId}`, `GET .../bank-connections/{id}/reception`, `POST .../inbound-address/resume` |
| Management key, or a session of a root administrator with `can_create_subtenants` | `POST/GET /v1/subtenants`, `GET/PATCH/DELETE /v1/subtenants/{id}`, `GET/POST /v1/subtenants/{id}/api-keys`, `DELETE /v1/subtenants/{id}/api-keys/{keyId}`, and the same bank connection, payment request, and inbound message routes under `/v1/subtenants/{id}/bank-connections`, `/v1/subtenants/{id}/payment-requests`, `/v1/subtenants/{id}/inbound-messages`, `/v1/subtenants/{id}/transactions`, and the forwarding routes under `/v1/subtenants/{id}/bank-connections/{connectionId}` |
| Provider signature (no key, no cookie) | `POST /v1/webhooks/resend/{instanceId}` and `POST /v1/webhooks/resend` |
| Session only | `GET/POST /v1/tenant/api-keys`, `DELETE /v1/tenant/api-keys/{id}`, `GET /v1/tenant/audit-events`, `GET /v1/subtenants/{id}/audit-events` |
| Sign-in and session | `GET /v1/auth/antiforgery`, `POST /v1/auth/login`, `GET /v1/auth/session`, `POST /v1/auth/select-tenant`, `POST /v1/auth/logout`, `POST /v1/auth/logout-all`, `POST /v1/auth/change-password` |

Browser requests carry two extra headers:

- `X-XSRF-TOKEN` is required on login and on every cookie-authenticated `POST`, `PATCH`, or `DELETE`. Get it from `GET /v1/auth/antiforgery`, and get a new one after login and after logout.
- `X-Pairrot-Tenant-Id` is the tenant the page is showing. It is required on tenant-scoped browser requests. If the session has switched to another tenant, for example in another tab, the request fails with `409 tenant_context_changed` instead of acting on the wrong tenant.

Errors use the format `{"error": {"code": "...", "message": "..."}}` with the statuses 400, 401, 403, 404, 409, 412, 413, 428, 429, and 503. Validation errors about one request field also include `"field"`, so a form can show the error next to that input. Unauthenticated browser requests get a `401` JSON response, never a redirect. A resource that exists but belongs to another tenant returns the same 404 as an unknown identifier. The full contracts are in the specs for [Feature 1](docs/features/001-tenants.md#4-api-contract), [Feature 2](docs/features/002-user-login-and-sessions.md#6-api-additions), [Feature 3](docs/features/003-bank-configuration-and-inbound-address.md#6-api-additions-and-permissions), [Feature 4](docs/features/004-payment-qr-generation.md#5-api-additions), [Feature 5](docs/features/005-expected-payments.md#5-api), [Feature 6](docs/features/006-inbound-email-reception.md#8-api-and-administrative-tools), and [Feature 7](docs/features/007-bank-message-parsing.md#8-api-and-local-spa).

### Bank connections

A bank connection is one tenant's registration of a Czech CZK account. Root tenants and subtenants use the same endpoints. Create one with `account_number` (`[prefix-]number/bank_code`, e.g. `19-2000145399/0800`), `iban`, or both, plus an optional `label`. The API checks the format and the check digits (CNB mod-11 and IBAN mod-97) and returns both forms. This does not prove that the account exists or who owns it.

- The normalized IBAN identifies the physical account (`bank_account_id` in responses, the same for every registration of it). At most one registration per account is active in the whole system. The tenant's own active one is `409 bank_connection_exists` with its `bank_connection_id`; another tenant's is a generic `409 account_unavailable` that names nobody. Archived registrations, in any tenant, deleted tenants included, do not block a new one.
- Only the label can be changed. `DELETE` archives a connection (`archived_reason: user`) and keeps its details, address, messages, and history; the account is released. Deleting a tenant archives its active connections (`tenant_deleted`). `POST .../bank-connections/{id}/restore` reactivates an archived connection of an active tenant, unless another registration of the account is active (`409 account_unavailable`); a suspended or deleted owner gets `409 tenant_not_active`. The current address is kept, mail held because the connection was archived is released and parsed, and repeating a restore changes nothing (`bank_connection.restored` is audited).
- `POST .../inbound-address` assigns an address on the default provider instance once. Retries and concurrent requests return the same address. `address_status` is `not_created`, `stub_created` (a `p_<random>@in.pairrot.test` test address that never receives mail, so do not configure it in internet banking), or `live_created`. With a live default instance, a connection that has only a stub address gets a new live address: the stub is retired but stays reserved, the response has `address_replaced: true`, and `bank_connection.address_replaced` is audited. A live address is never replaced or moved by a configuration change. For a provider that needs remote setup, the status is `pending` or `failed` (with `provisioning_error`) and the address is hidden until it is ready; repeating the request retries the same reservation.
- The response's `reception` shows separate facts: `last_message_received_at`, `last_content_stored_at`, and `last_content_error`. None of them proves that a bank sent an email or that a payment arrived.
- A parent can inspect a suspended child's connections but cannot change them. A deleted child's connections are kept read-only for history.
- An open request on an archived registration stays payable: a notification received through the tenant's later registration of the same account pays it, and the event carries the old `payment_request_id` and `external_id`. Fresh instructions for it stay refused (`409 bank_connection_archived`).
- The operator CLI shows every registration of an account across tenants (`accounts show`) and releases a squatted one (`accounts release`, `archived_reason: operator`).

### Payment QR codes

`POST .../bank-connections/{id}/payment-qr` returns payment data and a [QR Platba](https://qr-platba.cz/pro-vyvojare/specifikace-formatu/) (SPAYD) payload for an active connection. `POST .../payment-qr.png` returns the same payload as a PNG (`image/png`, download filename, `Cache-Control: no-store`). Both accept the same body and return `200`:

```json
{ "amount": 45000, "currency": "CZK", "variable_symbol": "0000123456", "message": "Yoga October" }
```

- `amount` is a whole number of minor units from 1 to 999999999, so `45000` is CZK 450.00. Fractions, exponents, and strings are rejected (`400`, `field: "amount"`).
- `currency` is required and must be `CZK`. `variable_symbol` is 1–10 ASCII digits, with leading zeros kept. `message` is trimmed and omitted when blank.
- The receiving account always comes from the saved connection. `iban`, `account_number`, `tenant_id`, or a ready-made payload in the body are unknown fields (`400`).
- The payload is always `SPD*1.0*ACC:<IBAN>*AM:<amount>*CC:CZK[*X-VS:<symbol>][*MSG:<message>]`, encoded in the QR code as UTF-8. The message goes in unchanged, Czech letters included, and is limited to 60 characters (`400 message_too_long`, never shortened). `*` and `%` are rejected (`400 invalid_message`) rather than percent-encoded: `*` separates fields, and George shows escapes literally and refuses to send the payment ([verification](docs/verification/004-payment-qr.md)).
- Archived connections (`409 bank_connection_archived`), suspended (`409 tenant_not_active`), and deleted (`409 tenant_deleted`) tenants cannot generate codes. Errors from the PNG endpoint are JSON too.
- Nothing is stored and no audit event is written. Repeating a request returns the same payload and does not reserve the variable symbol. The QR is rendered in-process by [QRCoder](https://github.com/codebude/QRCoder), black on white with a quiet zone, 8 px per module, error correction level M.
- A QR code for a real account prefills a real payment in a banking app. This stateless endpoint does not track whether the payment arrives; payment requests do (Features 5 and 9).

### Expected payments

`POST .../payment-requests` saves what a tenant expects to be paid, on which of its active bank connections, for which reference in the calling application:

```json
{
  "bank_connection_id": "<connection-id>",
  "external_id": "booking_123",
  "amount": 45000,
  "currency": "CZK",
  "message": "Yoga October",
  "description": "Reservation booking_123",
  "due_at": "2026-10-15T18:00:00+02:00"
}
```

- `external_id` (up to 200 characters) is required. Amount, currency, symbol, and message follow the payment QR rules above. `description` (up to 1000 characters) is internal and not in the QR. `due_at` must state its offset and is stored in UTC.
- A new `external_id` returns `201` with the record, `replayed: false`, and `payment_instructions` (account, payment fields, `format`, `generator_version`, and the SPAYD `payload`). Repeating the same creation returns the current record with `200` and `replayed: true`, also after later edits or cancellation; it never reopens anything. The same `external_id` with different original input returns `409 external_id_conflict`. The comparison uses a SHA-256 fingerprint of the original validated input, so omitted and `null` optional fields are equal, but `000123` and `123` are not.
- `variable_symbol` is optional (Feature 12). Omitted or `null`, Pairrot assigns a ten-digit `YYYYNNNNNN` symbol: the creation year in `Europe/Prague` followed by a six-digit sequence `000001`–`999999` per physical bank account and year (e.g. `2026000001`, `2026000002`, then `2027000001` in the new year). It skips reserved symbols and returns the saved value in the record and QR. Exhausting the annual series returns `409 variable_symbol_space_exhausted`, without wrapping or reusing symbols (a supplied unused symbol still works). Legacy symbols, frozen QR payloads, and idempotent replays remain unchanged; old cursors enter the current year's series automatically without a schema migration. A supplied symbol (1–10 digits, leading zeros kept) is used exactly as given; empty or invalid values are `400`, never silently replaced. Every creation on an account, either mode and through any of its registrations, holds that account's allocator lock until it commits.
- The creation mode is part of the original input: retry an automatic creation without `variable_symbol` (omitted and `null` are equal), and do not copy the assigned symbol back into the body; switching modes on a retry is `409 external_id_conflict`.
- Variable symbols are unique per physical bank account (Feature 13) with leading zeros ignored: `123` and `000123` collide (`409 variable_symbol_conflict`), cancelled requests and every earlier registration of the account included, also another tenant's. Generated symbols skip every symbol already saved on the account. A supplied high symbol does not move the automatic cursor. The same symbol on another account is allowed.
- The account number, IBAN, and payload are saved at creation and never regenerated. `GET .../{id}/payment-instructions` and `GET .../{id}/qr.png` return them (`Cache-Control: no-store`) only while the request is open (nothing paid yet), its bank connection is active, and its tenant is active; otherwise `409 payment_request_cancelled`, `payment_instructions_not_available` (something was paid), `bank_connection_archived`, `tenant_not_active`, or `tenant_deleted`. Archiving a connection does not cancel its requests.
- `GET .../{id}` returns an `ETag` with the record's `version`. `PATCH` changes only `description` and `due_at` of an open or partially paid request (`409 payment_request_settled` once paid or overpaid); other fields are `400 immutable_field`. `PATCH` and `POST .../cancel` (optional `reason`) need `If-Match: "<version>"`: missing is `428 precondition_required`, outdated is `412 precondition_failed`. Cancelling a cancelled request is a no-op and needs no version; a request with active allocations cannot be cancelled (`409 payment_request_has_allocations`). Nothing is ever deleted or reopened.
- `status` is `open`, `partially_paid`, `paid`, `overpaid`, or `cancelled`, derived from `paid_amount`, the sum of active allocations (see [Payment matching](#payment-matching-and-review)). `remaining_amount` and `overpaid_amount` follow from it; `remaining_amount` is `null` once cancelled. `is_overdue` is computed while something remains to be paid past `due_at` and never changes the status. The detail also lists `open_review_case_ids` about money that was allocated to the request.
- `GET .../payment-requests` lists newest first, 50 per page (`limit` up to 200, `cursor` = `next_cursor`), with optional `external_id`, `status`, `bank_connection_id`, `variable_symbol` (compared without leading zeros), and `overdue=true|false` filters.
- A parent can read a suspended or deleted child's requests for history; creating, editing, cancelling, and payment instructions need an active tenant. Creation, edits, and cancellation write `payment_request.*` audit events in the same transaction; reads do not. The standalone payment QR endpoints above still save nothing.

### Inbound email

Delivery adapters authenticate their own transport and hand a common `InboundDelivery` (`Pairrot.Core.Inbound`) to the shared intake. The intake, routing, storage, jobs, and later bank parsers never reference a provider type; a test checks this. Resend is the only live adapter so far: the webhook carries metadata and a content reference, and the worker retrieves the content later (deferred mode). An adapter may also deliver complete content inline, which is stored with the receipt and needs no job.

- The Resend webhook verifies the Svix signature and timestamp on the raw body with the official verifier before parsing. Invalid or expired signatures, tampered bodies, and malformed events get `400`; bodies over the limit `413`; a verified event that cannot be saved `503`, so Resend retries. Accepted, repeated, quarantined, and ignored event types get `200`. The same event ID with different content is `409` and changes nothing.
- Deliveries are unique per `(instance, delivery ID)` and messages per `(instance, source message ID)`, so repeated and concurrent deliveries create one receipt, one message, and one retrieval job. The same provider ID in another instance is another message.
- Ownership comes only from the delivered recipients and the address map. An unknown recipient, recipients of several connections, an address claimed through an instance it is not bound to, or missing recipient evidence quarantine the message. It then has no tenant and is visible only in the CLI. Mail to a retired address, an archived connection, or an inactive tenant or parent is kept for its owner with a `hold_reason`. Content naming other recipients than the delivery is held as `recipient_mismatch` and not shown to the tenant.
- The worker retrieves content with leases, so a stopped worker's job is resumed and a stale worker cannot save a second result. Temporary failures and `404` are retried with the configured delays (respecting `Retry-After`). Rejected credentials, oversized content, invalid responses, and exhausted retries fail visibly. `POST .../retry-fetch` queues the next attempt (`202`, or the job already queued), `409 content_already_available` for stored content, and `409 content_fetch_not_supported` for inline sources.
- The stored source is common text, HTML, sender headers, attachment metadata, and normalized SPF/DKIM/DMARC results with their origin. Missing evidence is `unknown`, never a pass. `GET .../source` returns it with `Cache-Control: no-store`. HTML is returned as text and the SPA shows it escaped; attachments and linked content are never downloaded. The provider's raw response is kept for diagnosis but not returned.
- Messages carry `source_kind` (`received` or `fixture`) and `environment` (`test` or `live`) from server configuration. Receiving an email never changes a payment request.
- Webhooks stay the normal path. A recovery check also lists each enabled instance's received messages, so webhooks that never arrived are recovered, even after a long outage. Resend lists with the paginated `GET /emails/receiving`; adapters without a listing (the stub) get no check and no API call. The check runs once in the background at startup and then hourly. Each instance has one sync state (`inbound_sync_states`) and at most one check at a time, through a job lease. A check covers everything from the last completed boundary minus the overlap, or from `sync_from` (the instance's first activation) the first time. It moves the boundary only after every listed message in range is recorded, and only while it still holds its lease. A failed page, a crash, or a lost lease leave the boundary where it was, and the next run repeats the range harmlessly.
- Listed messages go through the same intake with the receipt ID `discovery:<email id>`, so a later or concurrent webhook adds its own receipt to the same message and job. Content already stored is not fetched again. A listed message without recipients is quarantined until a verified delivery with recipients arrives. Provider errors and history beyond `HistoryRetention` are shown by `inbound sync`, never reported as covered.

Automated tests use signed fixture events, a fake Receiving API, and fake inline and deferred adapters. They prove transport handling, not bank origin. A controlled real email through a Resend account (Feature 6 acceptance criterion 8) has not been run yet: it needs Resend credentials, a receiving domain, and a public HTTPS webhook URL.

### Bank message parsing

A worker parses every stored message that is routed and not held. It does this whether or not a payment request exists. Messages stored before Feature 7, or routed later by an operator, are picked up by a sweep at startup and then every `MessageParsing:SweepInterval` (1 hour). The bank profile is chosen from the destination account's bank code. Profiles are code, so tenants cannot supply or choose one. Each profile only recognizes templates and extracts raw fields. Everything else is shared by all profiles (`Pairrot.Core.Parsing`) and reads only the common snapshot, never provider data:

- **Trust:** a sender is trusted only when the From address is an allowed bank identity and the receiving system reports `dmarc=pass`. Display names, forwarded copies, and `Authentication-Results` lines inside the email are not evidence. Missing evidence, `unknown`, or results with no receiving-system origin are untrusted.
- **Normalization:** amounts are parsed exactly into minor units, with no rounding and no floating point. A precision error or overflow requires review.
- **Required fields:** a missing amount, currency, or booking date requires review, as does an unparseable or ambiguous date or an invalid variable symbol.
- **Destination account:** a full destination account must equal the saved connection. A masked account counts only where the profile documents why it is sufficient.
- **Text and HTML:** HTML is reduced to inert text before matching. If the text and HTML parts disagree on payment-critical facts, the message requires review.
- **Outcomes:** debits, balances, and pending movements are `not_payment`. Corrections require review. Unknown templates and multi-movement notices are `unsupported`, with no retry loop.

Every attempt is an immutable `message_parse_runs` row. It records the source digest, profile and parser version, trust evidence, outcome and reasons, and the extracted candidate. A run with `parsed` or `duplicate` links the message to an `incoming_transactions` row, and a message supports at most one transaction:

- **Bank references:** a proven bank reference links several notices to one movement. The same reference with different facts disputes the existing movement instead of adding another.
- **No reference:** notices without a reference are never merged. A lookalike (same amount, date, and symbol) is recorded and flagged `review_required` together with the records it resembles.
- **Reprocessing:** `POST .../inbound-messages/{id}/reprocess` queues a new generation. If the facts are unchanged, the transaction ID stays. If the facts changed, or the source is now rejected, the transaction is marked `disputed` and nothing is overwritten.
- **Concurrency:** the run, the transaction changes, and the job completion are saved atomically under the job lease. Saves are serialized per physical account, so two workers cannot create the same movement, also through different registrations of the account; a lost race on the bank-reference index rolls the attempt back and evaluates it again.
- **Per account (Feature 13):** a bank reference is unique per account and profile, across tenants. The same tenant's notifications through another registration link to the existing movement or dispute it. A reference already recorded by another tenant creates no transaction: the message ends `review_required` with `account_movement_conflict` and a local-only review case. A lookalike without a reference is reviewed as a possible duplicate within the tenant; one of another tenant creates a local transaction that nobody can allocate until an operator decides. Comparison fingerprints use version `v2`, keyed by the account.

Transactions carry `evidence` (`live`, `test`, or `fixture`) from the message's instance. Parsing itself changes no payment request and sends no webhook. Only `accepted` records with `live` evidence are matched automatically ([Payment matching](#payment-matching-and-review)).

The selected bank is Česká spořitelna (`ceska_sporitelna` `1.0.0`, bank code 0800). The profile is built from one real George notification ("Informační zprávy → Příchozí platby", received 2026-10-07 through an automatic Gmail forward). The notification is kept sanitized as `tests/Fixtures/ceska-sporitelna/incoming-transfer-with-vs.html`. The bank sends HTML only: an opening sentence "na účet ‹holder› ‹account› právě dorazila platba ve výši ‹amount› Kč", then an "Informace o transakci" block with labeled lines.

- **Trust:** a `csas.cz` From address with DMARC `pass` is required. `csas.cz` publishes DMARC `p=reject`, and the Gmail forward kept the bank's DKIM.
- **Direction:** "Směr platby: příchozí" marks an incoming payment and "odchozí" a debit.
- **Amount and accounts:**
  - The amount is "Částka v měně účtu", cross-checked with the opening sentence. The balance line is never read.
  - The destination account is "Číslo účtu" and the sender account is "Číslo účtu protistrany".
- **Date:** "Datum a čas platby" gives the booking date and the time (Europe/Prague).
- **Optional fields:** "Variabilní symbol" and "Zpráva pro příjemce" are optional.
- **Bank reference:** the notification has none, so lookalike payments are flagged, not merged.
- **Forwarded copies:** a forward from an arbitrary mailbox is never trusted. Forwards from the tenant's registered mailbox are valid ([Forwarded notifications](#forwarded-notifications)).

Only an incoming payment with VS has been seen for real. Payments without VS, debits, corrections, counterparty names, and text parts are handled by the same labels but are not yet confirmed by samples. The unit tests run the real fixture and variants derived from it. The pipeline tests use a synthetic test profile (`tests/Shared/SyntheticBankProfile.cs`), which describes no real bank.

### Forwarded notifications

Česká spořitelna sends George's messages to one contact e-mail only, so a tenant usually keeps them in their own mailbox and forwards the payment notifications to the Pairrot address.

**Forwarding mailbox.** The tenant enters the mailbox by hand at the bank connection (`POST .../bank-connections/{id}/forwarding-sources`), and it is active at once. In the MVP, Pairrot does not verify that the tenant controls the mailbox and sends no e-mail. Registration and removal are audited, and the bank's own addresses and Pairrot addresses cannot be entered.

**Valid forwards.** A notification is valid, with its origin path recorded on the parse run and the transaction, when one of these holds:
- **Automatic forward, bank still authenticated:** a filter forwards it unchanged and DMARC still passes for the bank.
- **Automatic forward, mailbox authenticated:** the envelope sender is the registered mailbox, or its Gmail `+caf_` form, with SPF `pass`. This option is used only for domains listed in `Forwarding:SpfVerifiedForwarderDomains`, which is empty until a real sample proves it for a provider. Resend does not report the envelope sender, so over Resend only bank DMARC can authenticate an automatic forward.
- **Manual forward:** From is the registered mailbox with DMARC `pass` for its domain. The quoted original is parsed after the forwarding separator and quoted headers are removed.

A forward from a mailbox that was not entered, or one that fails these checks, requires review. Every Feature 7 content check still applies.

**Sender classes.** Before any content is retrieved, each routed message is classified from its delivery metadata as `bank`, `forwarding_confirmation`, `tenant_forward`, or `foreign`:
- **Foreign mail:** its content is never retrieved. Pairrot keeps only the sender domain, identities, and timestamps, which are deleted after 30 days. Foreign mail is hidden from the message list.
- **Pausing:** more than 5 foreign messages in 24 hours pause the address. Paused mail is also not stored, the pause is audited, and the SPA shows a warning.
- **Resuming and replacing:** the tenant resumes the address (`POST .../inbound-address/resume`) or gets a new one (`POST .../inbound-address`; the old address stays retired, reserved, and paused). If the address is paused again within 7 days of a resume, only an operator can resume it.
- **Forwarding confirmations:** mail-provider forwarding confirmations (default sender `forwarding-noreply@google.com`) are shown with their code at `GET .../bank-connections/{id}/reception`. They are never parsed and are deleted after 7 days.

**Setup status.** `GET .../reception` also reports the setup status, foreign-mail counts, and the latest bank notification with its path. The SPA shows all of this under each bank connection, with a Gmail and Seznam setup guide.

Resend still accepts and counts every message sent to the receiving domain, because it has no inbound blocklist. `inbound volume` shows daily volume per instance.

### Payment matching and review

A recorded transaction is matched in a background job (`match_transaction`), and a new payment request looks for receipts that arrived before it (`match_payment_request`). A sweep at startup and every `Matching:SweepInterval` (1 hour) queues transactions that never had a matching job and records review cases for older unresolved parser results.

- **Automatic matching is exact.** The transaction must be `accepted`, `live`, unallocated, in no open review case, and not blocked by a manual decision; the owner, its parent, and the connection must be active. Exactly one request of the same tenant and bank account, on any of its registrations, must have the same variable-symbol key (`000123` = `123`; no symbol never matches `0`), the same currency, status `open` with nothing paid, and exactly the received amount. The whole transaction is allocated. Nothing is inferred from names, messages, or similar amounts.
- **Explanations.** Otherwise the transaction keeps `matching.result` (`missing_variable_symbol`, `request_not_found`, `amount_mismatch`, `request_already_paid`, `request_cancelled`, `review_required`, `manual_allocation_only`, `not_live_evidence`, ...) and the suggested request. `GET .../transactions/{id}/matching-candidates` lists same-owner, same-account, same-currency requests with reasons and the resulting totals; `external_id`, `variable_symbol`, and `amount` search manually.
- **Allocations.** One transaction has at most one active allocation, always for its full amount (a partial unique index enforces it). Several transactions can pay one request. `paid_amount` is the sum of active allocations in checked 64-bit arithmetic; the status is `open` (0), `partially_paid`, `paid`, or `overpaid`. Allocations are never deleted: revoking keeps them with the author, time, and reason. Splitting a receipt, refunds, and a remaining-balance QR are out of scope.
- **Manual commands** (`Idempotency-Key` header required; versions in the body):
  - `POST .../transactions/{id}/allocations` `{payment_request_id, expected_transaction_version, expected_request_version, reason}` allocates by hand: partial payments, top-ups, overpayments, a missing or different symbol. Returns `201` with the allocation, transaction, and request.
  - `POST .../allocations/{id}/revoke` `{expected_allocation_version, expected_transaction_version, expected_request_version, reason}` recalculates the request and blocks automatic rematching of the released receipt. Reassigning is a revoke followed by a new allocation.
  - `POST .../transactions/{id}/ignore`, `/restore`, `/enable-auto-matching` `{expected_version, reason}`. Restoring does not match; only enabling queues a fresh attempt, and it cannot override a review, a duplicate or rejected record, or a manual-only confirmation. `POST .../transactions/{id}/match` queues another attempt (`202`).
  - A missing version is `428`, an outdated one `412`. Retrying the same key and input returns the stored response with `Idempotent-Replayed: true`; the same key with other input is `409 idempotency_key_conflict`. Failed commands store nothing.
- **Review cases** (`GET .../review-cases`, `GET .../review-cases/{id}`) cover `unverified_candidate` (a payment the parser extracted but could not trust, e.g. DMARC fail), `possible_duplicate` (lookalikes without a bank reference), `disputed` (reprocessing changed or rejected the facts, or a bank reference conflicts), and `account_movement_conflict` (Feature 13: the movement may be recorded by another tenant's registration of the account; only local evidence is shown, and only an operator decides it with `reviews resolve`: `reject` with reason code `duplicate_of_foreign_movement`, or `confirm_distinct` for a lookalike without a proven reference conflict; tenants get `409 operator_decision_required`). While a case is open its transactions cannot be allocated, and a transaction that enters one loses its allocation in the same database transaction (`transaction_review_required`). `POST .../review-cases/{id}/resolve` `{decision, reason, expected_version, expected_transaction_versions, canonical_transaction_id}` takes `confirm_payment`, `confirm_distinct`, `mark_duplicate`, `keep_existing`, or `reject`; `POST .../reopen` reopens a case and revokes allocations of its transactions again.
- **Manual confirmation** is a business decision, not authentication: the parse runs keep the provider's SPF/DKIM/DMARC results and the origin path, and the confirmed receipt can be allocated only by hand. A candidate can be confirmed only when it is complete (positive amount, the account's currency, booking date, matching destination account) and its only problems are about authentication; anything else must be fixed in the parser and reprocessed. Resolution never revives an old allocation, and reprocessing unchanged evidence does not reopen a decided case.
- **Payment changes.** Every change of a request's paid amount or status, including a second partial payment, a revocation, a system correction, and cancellation, appends an immutable `payment_request_changes` row with a per-request sequence and before/after totals (`GET .../payment-requests/{id}/changes`, `?after=<sequence>`). `GET .../payment-requests/{id}/allocations` lists active and revoked allocations. These rows are the input for Feature 10's webhooks; nothing is sent yet.
- **Evidence.** Test and fixture transactions never pay requests. `Matching:AllowTestEvidence=true` allows it only in Development and Testing (startup fails elsewhere) and keeps the test evidence on allocations and changes.

The local SPA shows matching results, candidates, manual allocation with a preview, review cases with their evidence and decisions, and each request's allocations and changes.

### Payment-status webhooks

Each tenant can have one endpoint (`POST .../webhook-endpoint` with `url`, `environment` `live` or `test`, and `scope`). It starts `paused`, and the response shows its signing secret `pwhsec_…` once. Routes:

- `/v1/tenant/...` manages the caller's own endpoint. A parent administrator's session may choose `scope: own_and_direct_children`; an ordinary tenant key may not (`403`).
- `/v1/management/...` is the same for a management key, e.g. NaLekci's backend.
- `/v1/subtenants/{id}/...` manages a child's own endpoint and never shows the parent's.

How it works:

- **Events.** Every Feature 009 payment change commits together with its immutable event (`payment_request.updated`, the change's ID and per-request sequence, `previous` and `current` totals, cause `allocation_created`, `allocation_revoked`, `transaction_review_required`, or `request_cancelled`) and one delivery for each subscriber at that moment: the owner's endpoint and the parent's when it includes children. Endpoints receive only their own environment. Edits, reads, QR codes, and creation retries create no events. Events for changes recorded before Feature 10 are materialized without deliveries; old history is sent only on request.
- **Delivery.** HTTPS POST, 10-second timeout, no redirects, no cookies, connections only to addresses validated as public at connect time. Headers: `Pairrot-Webhook-Id` (the delivery), `Pairrot-Webhook-Timestamp`, and `Pairrot-Webhook-Signature: v1,<key-id>,<base64 HMAC-SHA256 of "<id>.<timestamp>.<body>">`. Every attempt sends the same bytes with a fresh timestamp and signature.
- **Retries.** `2xx` is delivered. Network errors, timeouts, `408`, `425`, `429`, and `5xx` are retried 5 s, 60 s, and 1 h after each failure; `Retry-After` can only postpone. Four attempts per generation, then `failed`. Other `4xx` and redirects fail at once. An attempt is reserved before the request is sent; a worker that dies mid-send leaves an `outcome_unknown` attempt that counts and is retried. Nothing about a delivery changes a payment.
- **Holds.** A paused endpoint accumulates work and releases it when resumed. Changing the URL creates a new destination revision, and queued work for the old one is held as `destination_changed` until an explicit replay. Lost authority, an inactive tenant, or an unavailable key hold delivery and are rechecked before every send.
- **Management.** `PATCH .../webhook-endpoint` (`If-Match`) changes `url`, `scope`, or `state`. `POST .../rotate-secret` reveals a new key once; the old one keeps signing for `grace_period_seconds` (default 24 h; `0` revokes it at once). `POST .../test` sends a synthetic `webhook.test`, also while paused. `GET .../webhook-events` lists events for replay selection. `GET .../webhook-deliveries[/{id}]` shows state, attempts, and errors. `POST .../webhook-deliveries/{id}/retry` and `/abandon` take `expected_version` and `reason`. `POST .../webhook-endpoint/replay` takes up to 100 `event_ids`, `expected_destination_revision`, and `reason`. All POSTs need `Idempotency-Key`; a repeated secret-creating command returns metadata without the secret.

Receivers verify the raw body, deduplicate by event ID, and apply only a higher `sequence`; see [docs/webhooks-receiver.md](docs/webhooks-receiver.md).

### Attention events and the attention list

- **Event types** (Feature 16). Besides `payment_request.updated`: `transaction.recorded` (a receipt's first matching attempt completed, or it needs review; `matching.result` says `matched`, `amount_mismatch`, `request_not_found`, …), `transaction.updated` (allocated, revoked, ignored, restored, automatic matching enabled, review required or decided, marked duplicate, rejected, rematched), `review_case.opened` and `review_case.updated` (member added, evidence changed, resolved, reopened), and `inbound_address.paused` and `inbound_address.resumed`. Every event carries `data.subject_id`, `data.sequence` (per subject), and `data.cause`; payloads never carry sender fields, e-mail content, or free-text reasons. Every save that changes such a record records its event in the same transaction, whichever code path made the change. Events of erased tenants are not recorded. Test and fixture evidence produces test events, delivered only to test endpoints.
- **Subscriptions.** `event_types` on `POST`/`PATCH .../webhook-endpoint` (validated, `400 invalid_event_types`). Existing endpoints and endpoints created without it receive `payment_request.updated` only; `webhook.test` is always delivered. A change applies to events recorded after it. Events are recorded whether or not anyone subscribes; `GET .../webhook-events?type=` and `GET .../webhook-deliveries?type=` filter them, and replay of an unsubscribed type is `409 event_type_not_subscribed`.
- **Attention list.** `GET /v1/management/attention` (a management key or a parent administrator) covers the caller and its direct children; `GET /v1/tenant/attention` only the caller. Kinds: `unmatched_transaction` (accepted, live, unallocated, not ignored, in no open case, not matched), `review_case` (open), `paused_address`, `failed_delivery` (`failed` or `held`, of the caller's own endpoint), `overpaid_request` (while the ledger records an excess), and `overdue_request` (only with `kinds`). Each item has `kind`, `tenant_id`, `bank_connection_id`, `subject_id`, `since`, and a `summary` without sender data or free text. Oldest first, ordered by `(since, kind, tenant_id, subject_id)`; filters `kinds`, `tenant_id` (`404` outside the caller's scope), `since`; `limit` up to 200 and `cursor`. `GET .../attention/summary` counts per kind and per tenant. Items disappear when their cause is resolved; deleted and erased tenants contribute nothing. The SPA has an Attention tab.

### Sandbox mode

Feature 21 lets an integrating application test its whole payment flow on a developer's machine without a real bank. It is never available on a server: `Sandbox:Enabled=true` is refused outside Development and Testing, and without it the sandbox routes do not exist (`404`) and the sandbox bank profile is not registered.

```bash
docker compose up -d
dotnet run --project src/Pairrot.Admin -- migrate
dotnet run --project src/Pairrot.Admin -- sandbox seed --webhook-url http://localhost:5099/pairrot   # prints keys, a test webhook secret, and ready-to-run curl commands
dotnet run --project src/Pairrot.Api --launch-profile https-sandbox                                 # Sandbox__Enabled=true
```

A local webhook receiver needs `Webhooks:AllowedLocalDestinations` (for example `localhost:5099`). Then create a payment request as usual and simulate its payment:

```bash
curl -k -X POST https://localhost:7017/v1/tenant/payment-requests/<id>/sandbox/pay -H "Authorization: Bearer <tenant key>" -H "Content-Type: application/json" -d '{}'
```

- **A simulation is a real e-mail without the mail.** The notice is rendered in the bank's template and delivered through the connection's active inbound address, which must be on a test instance (the built-in `stub`; a connection without an address gets one). A live address is `409 sandbox_requires_test_address`, an archived connection `409 bank_connection_archived`. The message is `received`, `environment: test`, with transport and delivery evidence `sandbox` and sender authentication `pass` supplied by the sandbox adapter; its transaction has `test` evidence, and every payment change and event is `test`, so only **test** webhook endpoints receive them. Parsing, trust, duplicates, matching, review cases, and the journal are the production code.
- **Renderers.** For bank code 0800 the notice is the George HTML of `ceska_sporitelna`, parsed by the real profile (`bank_reference` is `400 field_not_supported_by_bank` there). Any other bank code gets the plain-text `pairrot_sandbox` profile (version `1.0.0`, sender `notifikace@banka.sandbox.pairrot.test`), which supports `bank_reference`, so duplicates (same reference, same facts) and disputes (same reference, other facts) can be tested.
- **`POST .../bank-connections/{id}/sandbox/incoming-payments`** `{amount, variable_symbol, message, payer_name, payer_account, booked_at, bank_reference, direction}`: `amount` (minor units, 1 to 999999999) is required; `direction` is `incoming` (default) or `outgoing` (a debit, parsed as `not_payment`). Unknown fields are `400`. `202` returns `{inbound_message_id, bank_connection_id, profile, status}`; the pipeline continues in the background jobs. An `Idempotency-Key` makes the delivery identity `sandbox:<tenant>:<key>`, so a retry creates one message (the same key with other input is `409 idempotency_key_conflict`); without it every call is a new notification, so "the bank sent it twice" can be simulated.
- **`POST .../payment-requests/{id}/sandbox/pay`** `{amount, variable_symbol, booked_at, payer_name}`, all optional: by default the unpaid remainder with the request's symbol through its connection. A cancelled request is allowed; a fully paid one needs an explicit `amount` (`409 payment_request_already_paid`). The response adds `payment_request_id`.
- **Matching stays exact.** Only a payment of exactly the requested amount with its symbol pays an open request automatically. A part payment, an overpayment, or a wrong symbol is recorded as an unallocated test transaction (`amount_mismatch`, `request_not_found`) and is allocated with the manual commands, like a real one. Test data stays off the live attention list.
- `GET /v1/sandbox` returns `{enabled, profiles}` and exists only in sandbox mode. Both simulation routes exist under `/v1/tenant` and `/v1/subtenants/{id}` with the authorization of the other bank connection routes, and each simulation writes the audit event `sandbox.payment_simulated` (connection, message, amount, symbol; no names).

### Erasure and retention

- `POST /v1/subtenants/{id}/erase` `{reason}` with an `Idempotency-Key`, by a management key or a parent administrator: `202` with the child and its `erasure` (`queued`, `running`, `done`, with counts per record type). Only a deleted child, and only `Erasure:MinimumAgeAfterDeletion` after its deletion (`409 tenant_not_deleted`, `409 erasure_too_early`). A repeat returns the same erasure with `Idempotent-Replayed: true`. `GET /v1/subtenants/{id}` shows `erasure` once requested and `erased_at` once done.
- **Before erasing, reconcile** anything that needs the original `external_id` or webhook history: erasure replaces caller references with `erased:<id>` tombstones and removes event payloads.
- The request sets the tenant's permanent collection barrier, abandons queued deliveries of its events at every destination (the parent's included, `abandon_reason: tenant_erased`), and disables its own endpoint and drops its signing secrets. The job then purges in batches; financial facts, symbols, statuses, sequences, allocations, and internal IDs stay, and `payments check` passes. Account snapshots are masked (`…99/0800`, `CZ…0800…99`); the account itself stays in `bank_accounts` (Feature 13), so its symbols and bank references stay unique and a new tenant can register it.
- Afterwards: stored e-mails are `content_state: erased` (`GET .../source`, `reprocess`, `retry-fetch`: `409 content_erased`), business changes and creation retries are `409 tenant_erased`, event bodies are `null` with `payload_erased_at` and cannot be retried or replayed (`409 event_payload_erased`). Mail to the erased tenant's address is acknowledged and deduplicated but only routing facts are kept. Webhook requests already sent cannot be recalled; the erasure reports `sent_webhook_copies_not_recalled`.
- **Payer-data retention** on every tenant: `Retention:PayerData` after reception, stored e-mails become `content_state: expired` (`409 content_expired`), and transactions lose their sender fields (`payer_data_purged_at`), whatever their allocation or review state. Amounts, symbols, references, and allocations stay; caller references of active tenants are not touched. A delivery older than the limit is stored the same way.
- Provider-held mail, backups, exported copies, and log sinks keep their own retention; erasure and retention purge the live database only. A restore must reapply completed erasures and retention before the data is used.

## Administrative CLI

Root tenants, their keys, and user accounts are managed from the command line, never over HTTP. There is no public signup, invitation flow, or emailed password reset.

```bash
dotnet run --project src/Pairrot.Admin -- migrate
dotnet run --project src/Pairrot.Admin -- setup [--name <name>] [--contact-email <email>] [--admin-email <email>]
dotnet run --project src/Pairrot.Admin -- keys list
dotnet run --project src/Pairrot.Admin -- keys issue --purpose tenant|management [--label <label>]
dotnet run --project src/Pairrot.Admin -- keys revoke <key-id>

dotnet run --project src/Pairrot.Admin -- users list
dotnet run --project src/Pairrot.Admin -- users create --email <email> [--tenant <tenant>]
dotnet run --project src/Pairrot.Admin -- users grant --email <email> --tenant <tenant>
dotnet run --project src/Pairrot.Admin -- users revoke --email <email> --tenant <tenant>
dotnet run --project src/Pairrot.Admin -- users disable --email <email>
dotnet run --project src/Pairrot.Admin -- users enable --email <email>
dotnet run --project src/Pairrot.Admin -- users reset-password --email <email>

dotnet run --project src/Pairrot.Admin -- inbound list [--filter attention|quarantined|failed|held|all] [--limit <n>]
dotnet run --project src/Pairrot.Admin -- inbound show <message-id>
dotnet run --project src/Pairrot.Admin -- inbound retry-routing <message-id>
dotnet run --project src/Pairrot.Admin -- inbound retry-fetch <message-id>
dotnet run --project src/Pairrot.Admin -- inbound sync
dotnet run --project src/Pairrot.Admin -- inbound sync run <instance-id>
dotnet run --project src/Pairrot.Admin -- inbound sync set-from <instance-id> <ISO time with offset>
dotnet run --project src/Pairrot.Admin -- inbound reprocess <message-id>
dotnet run --project src/Pairrot.Admin -- inbound parse-pending
dotnet run --project src/Pairrot.Admin -- inbound paused
dotnet run --project src/Pairrot.Admin -- inbound resume <address>
dotnet run --project src/Pairrot.Admin -- inbound volume [--days <n>]
dotnet run --project src/Pairrot.Admin -- payments sweep
dotnet run --project src/Pairrot.Admin -- payments check
dotnet run --project src/Pairrot.Admin -- accounts show <iban|account-number>
dotnet run --project src/Pairrot.Admin -- accounts release <connection-id> --reason <text>
dotnet run --project src/Pairrot.Admin -- reviews show <case-id>
dotnet run --project src/Pairrot.Admin -- reviews resolve <case-id> --decision <decision> --reason <text> --expected-version <n> \
    [--expected-transaction-versions <id=v,...>] [--reason-code <code>] [--canonical-transaction-id <id>] [--idempotency-key <key>]
dotnet run --project src/Pairrot.Admin -- webhooks backfill <tenant> --from <time> --to <time> [--apply]
dotnet run --project src/Pairrot.Admin -- tenants erase <tenant> --reason <text>
dotnet run --project src/Pairrot.Admin -- tenants erasure-status [<tenant>]
dotnet run --project src/Pairrot.Admin -- retention run
```

All commands accept `--operator <name>`, which is recorded in the audit log. `setup` and the `keys` commands also accept `--root <external-id>` (default `nalekci.cz`). A `<tenant>` is a tenant ID, a root external ID (`nalekci.cz`), or `root/child` (`nalekci.cz/studio_123`).

Passwords are prompted without echo, or read as one line from standard input when it is redirected. They are never printed or logged.

The `inbound` commands are the only global view of received mail, quarantined messages included. `inbound sync` shows, per instance, the last attempt and success, the covered-through time, the next due time, the discovered-message count, the last error, and any coverage gap. `set-from` moves recovery to an earlier start, e.g. when importing an existing receiving account. Routing can be retried only against the address map, never to a chosen tenant. Retries are recorded on the retrieval job and, for routed messages, in the tenant's audit history. The CLI needs the same `InboundEmail` configuration as the API to know which instances can retrieve content.

`webhooks backfill` previews the events of a tenant's webhook scope in a time range that its endpoint never received, with the destination and revision; `--apply` queues them.

`payments check` verifies that each paid amount equals its active allocations and that no disputed, duplicate, rejected, or reviewed receipt is counted; it exits with 1 when it finds a problem.

`accounts show` lists every registration of an account in every tenant, with status, archived reason, and dates; it is the only global view of an account. `accounts release` archives a registration as the operator, e.g. when the wrong tenant registered an account first, and audits it on the owner. `reviews show` prints a case with its local evidence and the versions a decision needs; `reviews resolve` decides it through the same service, versions, idempotency, and audit as the API, and is the only way to decide an `account_movement_conflict` case.

`tenants erase` erases any tenant, a root included, through the same job as the API: a tenant that is not deleted yet is deleted first (its subtenants must already be deleted), and there is no minimum age. `tenants erasure-status` lists erasures with their state and counts. `retention run` runs one retention pass now and prints the counts per provider instance. `inbound show` reports purged messages as `content erased` or `content expired`.

You can run `setup` again safely. It never creates a second root tenant, never changes an existing one, never replaces keys, and never resets an existing administrator's password. Disabling a user or resetting their password revokes all their sessions, but not the tenant's API keys.

## Database migrations

```bash
dotnet ef migrations add <Name> --project src/Pairrot.Infrastructure --output-dir Persistence/Migrations
dotnet run --project src/Pairrot.Admin -- migrate
```

The tenant tables mirror [docs/features/001-tenants-schema.sql](docs/features/001-tenants-schema.sql), including its check constraints and unique indexes. Feature 2 adds these tables:

- `users` (ASP.NET Core Identity storage)
- `tenant_memberships`
- `user_sessions`
- `user_audit_events`
- `data_protection_keys`

Feature 2 also gives audit events a user actor, with exactly one actor per event. Feature 3 adds `bank_connections` and records connection creation, label changes, address assignment, and archiving in `tenant_audit_events`. Feature 5 adds `payment_requests`. Its composite foreign key `(tenant_id, bank_connection_id)` references `bank_connections (tenant_id, id)`, so a request cannot point at another tenant's account. Unique indexes cover `(tenant_id, external_id)` and `(bank_connection_id, variable_symbol_key)`, and check constraints cover the amount range, currency, symbol format, state, and cancellation timestamps.

Feature 12 adds `variable_symbol_allocators` (one row per bank connection, created on first use, `next_candidate` from `1` to the exhausted sentinel `10000000000`). `payment_requests` gains `variable_symbol_source` (`supplied` or `automatic`; existing rows are `supplied`) and a stored `variable_symbol_number` with an index for the allocator's gap search. Stored symbols, keys, payloads, and fingerprints are unchanged; automatic creations use a separate `v2` fingerprint. Deploy the migration and the application together, without older instances still writing.

Feature 13 adds `bank_accounts` (one row per normalized IBAN, never deleted) and `bank_account_id` on bank connections, payment requests and their changes, transactions, parse runs, source links, review cases and members, and allocations. Every record's connection reference includes the tenant and the account, and references between records use the tenant and the account, so the database rejects links across owners or accounts while allowing links across one account's registrations. Allocations keep the transaction's and the request's connection. `bank_connections_iban_unique` is replaced by a partial unique index on active registrations; `archived_reason` is added (`user` for archived rows; active connections of deleted tenants are archived as `tenant_deleted`). The allocator, the variable-symbol index, the bank-reference index, and the fingerprint index move to the account, and every comparison fingerprint is recomputed as `v2`. The migration runs in one transaction and ends with a self-check (accounts, allocators, one active registration per account, consistent account IDs, unique references, fingerprints, and the `payments check` rules); any failure rolls it back. Record IDs, financial facts, symbols, and allocations are unchanged. Deploy it together with the application, without older instances still writing.

Feature 14 adds `tenant_erasures` (one per tenant: state, requester, counts, limitations; the reason is removed when done) and `erasure_requested_at`/`erased_at` on `tenants`, `erased_at` on bank connections, forwarding sources, payment requests, webhook endpoints, and transactions, `payload_erased_at` on webhook events, `purged_at` on parse runs, and `payer_data_purged_at` on transactions. Inbound messages gain the content states `expired` and `erased`, which keep no sender or content fields. Webhook URLs, event bodies, QR payloads, command responses, and forwarding addresses become nullable; check constraints allow masked account snapshots and missing payloads only on erased rows. Existing rows are unchanged.

Feature 16 adds `event_types` to `webhook_endpoints` (existing endpoints: `payment_request.updated`) and `subject_id` to `webhook_events` (existing payment events get their payment request), a unique index on subject and sequence, and partial indexes for unallocated transactions and overpaid requests. No events are created for existing records; a transaction's first event after the migration is its `transaction.recorded`.

Feature 6 adds `inbound_addresses`, `inbound_delivery_receipts`, `inbound_messages`, `background_jobs`, and `inbound_sync_states`, and moves the Feature 3 address columns of `bank_connections` into `inbound_addresses` without changing any address or owner. Addresses are globally unique, retired ones included, and at most one is active per connection; an address on a reserved test domain can never be live. Messages reference their tenant, connection, and address through composite foreign keys, so an owner always matches, and an unrouted message has no owner at all. Jobs allow one active job per operation and target.

Feature 7 adds `message_parse_runs` (unique per message and job generation), `incoming_transactions` (unique per connection, profile, and non-null bank reference; positive amounts in minor units; typed money, identity, and date columns), and `transaction_sources` (one per message). It also adds the latest parse run, its outcome, and the parse time to `inbound_messages`. Runs, transactions, and source links carry their tenant and connection, and every reference between them, and to the message, includes the owner. The database therefore rejects cross-tenant links, a source link whose run read another message, and a latest run that belongs to another message. There is no allocation table and no change to payment requests.

Feature 8 adds `forwarding_sources` (owner-consistent reference to the connection, unique per connection and address among non-removed sources). `inbound_addresses` gains a processing state (`active`, `paused_foreign_mail`) separate from `active`/`retired`, with pause and resume times. `inbound_messages` gains the sender class, sender domain, a retention deadline, and the content state `skipped`. A check constraint guarantees that foreign or paused messages keep no subject, sender, body, headers, or raw content. `incoming_transactions` gains `origin_path`.

Feature 9 adds `payment_allocations` (composite owner/account references to both the transaction and the request, a partial unique index allowing one active allocation per transaction), `payment_request_changes` (unique per request and sequence), `payment_review_cases` with their members and immutable `payment_review_decisions`, and `manual_command_receipts` (unique per owner and `Idempotency-Key`). `payment_requests` gains `paid_amount` and `change_sequence`, and a check constraint ties the status to the paid amount. `incoming_transactions` gains the handling state, automatic-matching block, manual confirmation, canonical duplicate reference, the latest matching result, and a version. Existing requests keep their status with nothing paid; the matching sweep records cases for older unresolved results and queues existing transactions.

Features 10 and 11 add `webhook_endpoints` (one per tenant), immutable `webhook_destination_revisions`, `webhook_signing_keys` (encrypted secrets, at most one current and one retiring key), `webhook_events` (immutable bytes and digest, unique per payment change), `webhook_deliveries` (unique per event and endpoint, with state, generation, attempt counters, due time, and lease), and `webhook_delivery_attempts` (unique per delivery, generation, and attempt number, so a generation cannot exceed four attempts). Nothing is deleted; a retired key loses only its secret.

## Tests

```bash
dotnet test
```

Integration tests start their own PostgreSQL container through Testcontainers, so Docker must be running. They do not use the development database. Browser flows are tested over HTTPS with a cookie container, the antiforgery header, and the tenant header, the same way the SPA sends them. Inbound tests sign webhooks with the Svix library and run them through the real verifier, use a fake Receiving API behind the real HTTP client, and move an adjustable clock to test retries and leases. Payment QR PNGs are decoded with ZXing.Net, which shares no code with the QRCoder renderer, and SPAYD payloads are checked against the published example with a separate reader.
