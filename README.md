# Pairrot

Pairrot matches incoming bank payments to expected payments and reports the results to its customers. It runs as a single ASP.NET Core application that serves an API and also runs background processing. Its first customer is [NaLekci](https://nalekci.cz).

Implemented so far:

- **Feature 1: tenants, subtenants, and API access** ([spec](docs/features/001-tenants.md))
- **Feature 2: user login, sessions, and API key management** ([spec](docs/features/002-user-login-and-sessions.md))
- **Feature 3: bank configuration and an inbound email address** ([spec](docs/features/003-bank-configuration-and-inbound-address.md)), with a local stub instead of Resend
- **Feature 4: payment QR generation** ([spec](docs/features/004-payment-qr-generation.md)): Czech QR Platba (SPAYD) payloads and PNG codes for saved accounts
- **Feature 5: expected payments** ([spec](docs/features/005-expected-payments.md)): saved payment requests with a reservation reference, frozen payment instructions, edits, and cancellation
- **Feature 6: inbound email reception** ([spec](docs/features/006-inbound-email-reception.md)): a provider-independent intake with Resend as the first live adapter, verified webhooks, a startup and hourly recovery check for missed webhooks, routing to bank connections, and stored source content retrieved by a background worker. Live reception through a real Resend account is not verified yet (see [below](#inbound-email))

The target architecture is described in [docs/architektura.md](docs/architektura.md) (in Czech).

Designed next, not yet implemented:

- [Feature 007: Česká spořitelna messages to incoming transactions](docs/features/007-bank-message-parsing.md)

## Tech stack

- .NET 10, ASP.NET Core minimal APIs
- EF Core 10 with Npgsql (PostgreSQL), snake_case naming
- ASP.NET Core Identity (password storage), cookie authentication, antiforgery, Data Protection keys in PostgreSQL
- PostgreSQL 17 in Docker for local development; [Neon](https://neon.tech) is the planned production database
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

The database connection is read from `ConnectionStrings:Pairrot` (environment variable: `ConnectionStrings__Pairrot`). Local development values are in `appsettings.Development.json`. To use Neon or any other PostgreSQL, set the environment variable; no code changes are needed:

```bash
ConnectionStrings__Pairrot="Host=<neon-host>;Database=pairrot;Username=<user>;Password=<password>;SSL Mode=Require"
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

The API process runs a background worker for durable jobs in PostgreSQL. `BackgroundJobs` settings: `Enabled` (`true`), `PollInterval` (`00:00:05`), `LeaseDuration` (`00:05:00`), `MaxAttempts` (`6`), and `RetryDelays` (`00:01:00`, `00:05:00`, `00:30:00`, `02:00:00`, `06:00:00`).

Cookie-protection (Data Protection) keys are stored in the `data_protection_keys` table, so sessions survive restarts and work across instances that share the database.

## API overview

JSON uses snake_case fields. There are two ways to authenticate:

- **API keys** for integrations: `Authorization: Bearer <key>`, format `prt_<key-id>_<secret>`. Pairrot stores only a SHA-256 hash of the secret, so a key's secret is shown only when the key is created.
- **Browser sessions** for people: email and password, then an `HttpOnly` cookie that identifies a server-side session row in PostgreSQL. The session, the user, and the membership are re-checked on every request.

If an `Authorization` header is present, only the key is checked; an invalid key never falls back to the cookie.

| Caller | Endpoints |
| --- | --- |
| Tenant key, or a session with the tenant selected | `GET /v1/tenant`, `GET/PATCH /v1/tenant/configuration`, `GET/POST /v1/tenant/bank-connections`, `GET/PATCH/DELETE /v1/tenant/bank-connections/{id}`, `POST /v1/tenant/bank-connections/{id}/inbound-address`, `POST /v1/tenant/bank-connections/{id}/payment-qr` and `.../payment-qr.png`, `GET/POST /v1/tenant/payment-requests`, `GET/PATCH /v1/tenant/payment-requests/{id}`, `POST .../{id}/cancel`, `GET .../{id}/payment-instructions` and `.../{id}/qr.png`, `GET /v1/tenant/inbound-messages`, `GET .../inbound-messages/{id}`, `GET .../{id}/source`, `POST .../{id}/retry-fetch` |
| Management key, or a session of a root administrator with `can_create_subtenants` | `POST/GET /v1/subtenants`, `GET/PATCH/DELETE /v1/subtenants/{id}`, `GET/POST /v1/subtenants/{id}/api-keys`, `DELETE /v1/subtenants/{id}/api-keys/{keyId}`, and the same bank connection, payment request, and inbound message routes under `/v1/subtenants/{id}/bank-connections`, `/v1/subtenants/{id}/payment-requests`, and `/v1/subtenants/{id}/inbound-messages` |
| Provider signature (no key, no cookie) | `POST /v1/webhooks/resend/{instanceId}` and `POST /v1/webhooks/resend` |
| Session only | `GET/POST /v1/tenant/api-keys`, `DELETE /v1/tenant/api-keys/{id}`, `GET /v1/tenant/audit-events`, `GET /v1/subtenants/{id}/audit-events` |
| Sign-in and session | `GET /v1/auth/antiforgery`, `POST /v1/auth/login`, `GET /v1/auth/session`, `POST /v1/auth/select-tenant`, `POST /v1/auth/logout`, `POST /v1/auth/logout-all`, `POST /v1/auth/change-password` |

Browser requests carry two extra headers:

- `X-XSRF-TOKEN` is required on login and on every cookie-authenticated `POST`, `PATCH`, or `DELETE`. Get it from `GET /v1/auth/antiforgery`, and get a new one after login and after logout.
- `X-Pairrot-Tenant-Id` is the tenant the page is showing. It is required on tenant-scoped browser requests. If the session has switched to another tenant, for example in another tab, the request fails with `409 tenant_context_changed` instead of acting on the wrong tenant.

Errors use the format `{"error": {"code": "...", "message": "..."}}` with the statuses 400, 401, 403, 404, 409, 412, 413, 428, 429, and 503. Validation errors about one request field also include `"field"`, so a form can show the error next to that input. Unauthenticated browser requests get a `401` JSON response, never a redirect. A resource that exists but belongs to another tenant returns the same 404 as an unknown identifier. The full contracts are in the specs for [Feature 1](docs/features/001-tenants.md#4-api-contract), [Feature 2](docs/features/002-user-login-and-sessions.md#6-api-additions), [Feature 3](docs/features/003-bank-configuration-and-inbound-address.md#6-api-additions-and-permissions), [Feature 4](docs/features/004-payment-qr-generation.md#5-api-additions), [Feature 5](docs/features/005-expected-payments.md#5-api), and [Feature 6](docs/features/006-inbound-email-reception.md#8-api-and-administrative-tools).

### Bank connections

A bank connection is a Czech CZK account that belongs to one tenant for its whole life. Root tenants and subtenants use the same endpoints. Create one with `account_number` (`[prefix-]number/bank_code`, e.g. `19-2000145399/0800`), `iban`, or both, plus an optional `label`. The API checks the format and the check digits (CNB mod-11 and IBAN mod-97) and returns both forms. This does not prove that the account exists or who owns it.

- The IBAN is the account's identity. One account can belong to only one connection in the whole system, archived ones included. Another tenant's account gets a generic `409 account_unavailable`.
- Only the label can be changed. `DELETE` archives a connection and keeps its details and address.
- `POST .../inbound-address` assigns an address on the default provider instance once. Retries and concurrent requests return the same address. `address_status` is `not_created`, `stub_created` (a `p_<random>@in.pairrot.test` test address that never receives mail, so do not configure it in internet banking), or `live_created`. With a live default instance, a connection that has only a stub address gets a new live address: the stub is retired but stays reserved, the response has `address_replaced: true`, and `bank_connection.address_replaced` is audited. A live address is never replaced or moved by a configuration change. For a provider that needs remote setup, the status is `pending` or `failed` (with `provisioning_error`) and the address is hidden until it is ready; repeating the request retries the same reservation.
- The response's `reception` shows separate facts: `last_message_received_at`, `last_content_stored_at`, and `last_content_error`. None of them proves that a bank sent an email or that a payment arrived.
- A parent can inspect a suspended child's connections but cannot change them. A deleted child's connections are kept read-only for history.

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
- A QR code for a real account prefills a real payment in a banking app. Pairrot does not yet track whether the payment arrives.

### Expected payments

`POST .../payment-requests` saves what a tenant expects to be paid, on which of its active bank connections, for which reference in the calling application:

```json
{
  "bank_connection_id": "<connection-id>",
  "external_id": "booking_123",
  "amount": 45000,
  "currency": "CZK",
  "variable_symbol": "2026000123",
  "message": "Yoga October",
  "description": "Reservation booking_123",
  "due_at": "2026-10-15T18:00:00+02:00"
}
```

- `external_id` (up to 200 characters) and `variable_symbol` (1–10 digits) are required. Amount, currency, symbol, and message follow the payment QR rules above. `description` (up to 1000 characters) is internal and not in the QR. `due_at` must state its offset and is stored in UTC.
- A new `external_id` returns `201` with the record, `replayed: false`, and `payment_instructions` (account, payment fields, `format`, `generator_version`, and the SPAYD `payload`). Repeating the same creation returns the current record with `200` and `replayed: true`, also after later edits or cancellation; it never reopens anything. The same `external_id` with different original input returns `409 external_id_conflict`. The comparison uses a SHA-256 fingerprint of the original validated input, so omitted and `null` optional fields are equal, but `000123` and `123` are not.
- Variable symbols are unique per bank connection with leading zeros ignored: `123` and `000123` collide (`409 variable_symbol_conflict`), cancelled requests included. The same symbol on another account is allowed. Symbols are never generated or reused.
- The account number, IBAN, and payload are saved at creation and never regenerated. `GET .../{id}/payment-instructions` and `GET .../{id}/qr.png` return them (`Cache-Control: no-store`) only while the request is open, its bank connection is active, and its tenant is active; otherwise `409 payment_request_cancelled`, `bank_connection_archived`, `tenant_not_active`, or `tenant_deleted`. Archiving a connection does not cancel its requests.
- `GET .../{id}` returns an `ETag` with the record's `version`. `PATCH` changes only `description` and `due_at` of an open request; other fields are `400 immutable_field`. `PATCH` and `POST .../cancel` (optional `reason`) need `If-Match: "<version>"`: missing is `428 precondition_required`, outdated is `412 precondition_failed`. Cancelling a cancelled request is a no-op and needs no version. Nothing is ever deleted or reopened.
- `status` is `open` or `cancelled`. `is_overdue` is computed for an open request past `due_at` and never changes the status. `paid_amount` is always 0 and `remaining_amount` is the amount, or `null` once cancelled, until payment matching exists.
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
```

All commands accept `--operator <name>`, which is recorded in the audit log. `setup` and the `keys` commands also accept `--root <external-id>` (default `nalekci.cz`). A `<tenant>` is a tenant ID, a root external ID (`nalekci.cz`), or `root/child` (`nalekci.cz/studio_123`).

Passwords are prompted without echo, or read as one line from standard input when it is redirected. They are never printed or logged.

The `inbound` commands are the only global view of received mail, quarantined messages included. `inbound sync` shows, per instance, the last attempt and success, the covered-through time, the next due time, the discovered-message count, the last error, and any coverage gap. `set-from` moves recovery to an earlier start, e.g. when importing an existing receiving account. Routing can be retried only against the address map, never to a chosen tenant. Retries are recorded on the retrieval job and, for routed messages, in the tenant's audit history. The CLI needs the same `InboundEmail` configuration as the API to know which instances can retrieve content.

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

Feature 6 adds `inbound_addresses`, `inbound_delivery_receipts`, `inbound_messages`, `background_jobs`, and `inbound_sync_states`, and moves the Feature 3 address columns of `bank_connections` into `inbound_addresses` without changing any address or owner. Addresses are globally unique, retired ones included, and at most one is active per connection; an address on a reserved test domain can never be live. Messages reference their tenant, connection, and address through composite foreign keys, so an owner always matches, and an unrouted message has no owner at all. Jobs allow one active job per operation and target.

## Tests

```bash
dotnet test
```

Integration tests start their own PostgreSQL container through Testcontainers, so Docker must be running. They do not use the development database. Browser flows are tested over HTTPS with a cookie container, the antiforgery header, and the tenant header, the same way the SPA sends them. Inbound tests sign webhooks with the Svix library and run them through the real verifier, use a fake Receiving API behind the real HTTP client, and move an adjustable clock to test retries and leases. Payment QR PNGs are decoded with ZXing.Net, which shares no code with the QRCoder renderer, and SPAYD payloads are checked against the published example with a separate reader.
