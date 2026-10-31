# Pairrot

Pairrot pairs incoming bank payments with the payments you expect. Your application tells Pairrot what it wants to be paid, Pairrot gives you a QR code for the payer, reads the bank's payment notifications, works out which request each payment belongs to, and tells your application by webhook when a request is paid.

It is built for Czech CZK accounts and needs no bank API, open-banking contract, or access to internet banking: it works from the e-mail notifications the bank already sends. Its first customer is [NaLekci](https://nalekci.cz).

## What it can do

**Payment requests and QR codes**
- Save an expected payment with your own reference (`external_id`), an amount, a due date, and a message for the payer.
- Assign a variable symbol automatically (`2026000001`, `2026000002`, … per account and year) or use your own.
- Generate Czech [QR Platba](https://qr-platba.cz/) (SPAYD) payloads and PNG codes that work in Czech banking apps. The payment instructions are saved when the request is created and never change.
- Creation is idempotent: retrying with the same `external_id` returns the same request instead of creating a second one.

**Reading bank notifications**
- Each bank account gets its own Pairrot e-mail address. The bank's notifications are sent or forwarded there.
- Pairrot checks that a notification really comes from the bank (DMARC), reads the amount, variable symbol, payer, date, and message, and records an incoming transaction.
- Notifications forwarded from the account owner's mailbox (Gmail, Seznam, and others) are accepted when they can be authenticated. Mail that is not from the bank or a registered mailbox is never stored, and an address that receives spam is paused.
- Missed delivery webhooks are recovered by a regular check of the mail provider, so an outage loses no payments.

**Matching payments**
- A payment with exactly the requested amount and variable symbol pays its request automatically. Nothing is guessed from names, messages, or similar amounts.
- Everything else (a partial payment, an overpayment, a missing or wrong symbol, a payment that arrived before its request) waits for a person, who allocates it by hand with the help of suggested candidates.
- Requests are `open`, `partially_paid`, `paid`, `overpaid`, or `cancelled`. Several payments can pay one request, and every change is kept in an append-only history.
- Possible duplicates, untrusted notifications, and conflicting reports go to review cases instead of silently paying anything.

**Notifying your application**
- Signed webhooks for payment-status changes, plus optional events for new transactions, review cases, and paused addresses.
- Retries with backoff, a delivery history, manual retry and replay, and signing-key rotation.
- An attention list of everything that needs a person: unmatched payments, open review cases, paused addresses, failed webhooks, and overpaid or overdue requests.

**Multiple customers**
- Tenants and subtenants: a platform (such as NaLekci) manages its own customers (for example individual studios), each with its own accounts, requests, and API keys. The parent can see and manage its direct children.
- API keys for integrations, password sign-in with server-side sessions for people, and an audit log of every change.
- Erasure of a deleted subtenant's personal data, and automatic removal of payer data two years after it was received, while amounts and payment history stay.

**Testing**
- A local sandbox mode simulates incoming payments and runs them through the real pipeline, so an integration can be tested end to end without a bank. See [Sandbox mode](#sandbox-mode).

## Supported banks

| Bank | Bank code | Status |
| --- | --- | --- |
| Česká spořitelna (George) | 0800 | **Supported** |
| Other Czech banks | | Not supported yet |

**Česká spořitelna.** Pairrot reads George's "Příchozí platba" notifications (*Informační zprávy → Příchozí platby* in George settings). George sends them to one contact e-mail only, so the usual setup is an automatic forward from that mailbox to the Pairrot address; the local SPA has a setup guide for Gmail and Seznam. The parser was built from a real notification received through an automatic Gmail forward. So far only an incoming payment with a variable symbol has been seen for real; payments without a symbol and other variants use the same template but are not yet confirmed by real samples. George notifications carry no bank reference, so two identical payments on the same day go to review as possible duplicates instead of being merged.

**Other banks.** You can register an account at any Czech bank and generate payment requests and QR codes for it, but Pairrot does not yet recognize its notifications, so payments to it are not matched. Each new bank needs a parser built from real notifications. A machine-readable bank catalog is [planned](docs/features/015-supported-banks-and-reception-readiness.md).

## How it works

```
your app ──create request──▶ Pairrot ──QR code──▶ payer pays in their banking app
                                                            │
bank ──notification e-mail──▶ (forwarded) ──▶ Pairrot address
                                                            │
                        parse ─▶ transaction ─▶ match ─▶ request paid ─▶ webhook to your app
```

1. Your application registers its bank account and gets a notification address for it.
2. The account owner sets up the bank (or their mailbox) to send payment notifications to that address.
3. Your application creates a payment request and shows its QR code to the payer.
4. When the bank's notification arrives, Pairrot verifies and parses it, records the transaction, and matches it.
5. Your application receives a signed `payment_request.updated` webhook, or reads the request's status over the API.

## Current status

Pairrot runs in production at `api.pairrot.chytre.digital`. Not yet verified end to end:

- live reception through a real [Resend](https://resend.com/) receiving account (the inbound adapter is tested with signed fixtures and a fake API),
- an automatic match against a real Česká spořitelna notification,
- real forwarding samples from Seznam (Gmail is confirmed),
- NaLekci's own webhook receiver.

The design and planned work are described in Czech in [docs/architektura.md](docs/architektura.md) and the [feature specifications](docs/features/).

## Integrating

JSON uses snake_case fields. Integrations authenticate with an API key, `Authorization: Bearer prt_<key-id>_<secret>`; people sign in with e-mail and password and get a session cookie. Errors are `{"error": {"code": "...", "message": "..."}}`.

| Area | Main endpoints |
| --- | --- |
| Bank accounts | `GET/POST /v1/tenant/bank-connections`, `POST .../{id}/inbound-address`, `GET .../{id}/reception`, `GET/POST .../{id}/forwarding-sources` |
| Payment requests | `GET/POST /v1/tenant/payment-requests`, `GET/PATCH .../{id}`, `POST .../{id}/cancel`, `GET .../{id}/payment-instructions`, `GET .../{id}/qr.png` |
| Stateless QR codes | `POST /v1/tenant/bank-connections/{id}/payment-qr` and `.../payment-qr.png` |
| Transactions and review | `GET /v1/tenant/transactions`, `POST .../{id}/allocations`, `GET /v1/tenant/review-cases`, `POST .../{id}/resolve` |
| Webhooks | `POST/PATCH /v1/tenant/webhook-endpoint`, `.../rotate-secret`, `.../test`, `GET .../webhook-deliveries` |
| Attention | `GET /v1/tenant/attention`, `GET /v1/management/attention` |
| Subtenants (management key) | `GET/POST /v1/subtenants`, the same routes under `/v1/subtenants/{id}/...`, `POST /v1/subtenants/{id}/erase` |

- The full behavior of every endpoint is in the [API reference](docs/api.md).
- How to receive and verify webhooks, with a .NET example: [docs/webhooks-receiver.md](docs/webhooks-receiver.md).
- Example requests: [src/Pairrot.Api/Pairrot.Api.http](src/Pairrot.Api/Pairrot.Api.http).

## Sandbox mode

Sandbox mode lets you test your whole payment flow on your own machine without a real bank. It is available only in the Development and Testing environments; the API refuses to start with it anywhere else.

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

The simulated payment is rendered as the bank's own notification (George HTML for bank code 0800, a simple test format for other bank codes) and goes through the real parsing, matching, review, and webhooks. Everything it creates is marked as test data and is delivered only to test webhook endpoints. `POST .../bank-connections/{id}/sandbox/incoming-payments` simulates any incoming payment, including partial payments, overpayments, wrong symbols, and duplicates. Details are in the [API reference](docs/api.md#sandbox-mode).

## Tech stack

- .NET 10, ASP.NET Core minimal APIs
- EF Core 10 with Npgsql (PostgreSQL), snake_case naming
- ASP.NET Core Identity (password storage), cookie authentication, antiforgery, Data Protection keys in PostgreSQL
- PostgreSQL 17 in Docker, locally (`docker-compose.yml`) and in production
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
docs/                      API reference, specifications (Czech), and feature scopes
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

API-key clients can use plain HTTP on port 5034. Browser sessions need HTTPS because the session cookie is `Secure`.

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

Erasure and retention: `Erasure:MinimumAgeAfterDeletion` (`7.00:00:00`; `0` disables the wait), `Erasure:BatchSize` (`500`), `Retention:PayerData` (`730.00:00:00`), and `Retention:Enabled` (`true`). The retention pass runs at startup and then daily in the API process.

Outgoing webhooks are configured in the `Webhooks` section: `DispatcherEnabled` (`true`), `PollInterval` (`01:00:00`, the longest sleep; saved deliveries and endpoint changes wake the dispatcher, which otherwise sleeps until the next due time), `LeaseDuration` (`00:01:00`), `MaxParallel` (`8`, with at most one request per endpoint in flight), `MaintenanceInterval` (`01:00:00`), and `AllowedLocalDestinations` (empty). The last one lists `host:port` entries, e.g. `localhost:5099`, that may use HTTP and a private address for a local test receiver; the API refuses to start with it outside Development and Testing. Payment matching uses `Matching:SweepInterval` (`01:00:00`) and `Matching:AllowTestEvidence` (`false`, Development and Testing only). `Sandbox:Enabled` (`false`, Development and Testing only; the API and the CLI refuse to start otherwise) turns on sandbox mode and implies `Matching:AllowTestEvidence`.

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

## Tests

```bash
dotnet test
```

Integration tests start their own PostgreSQL container through Testcontainers, so Docker must be running. They do not use the development database. Browser flows are tested over HTTPS with a cookie container, the antiforgery header, and the tenant header, the same way the SPA sends them. Inbound tests sign webhooks with the Svix library and run them through the real verifier, use a fake Receiving API behind the real HTTP client, and move an adjustable clock to test retries and leases. Payment QR PNGs are decoded with ZXing.Net, which shares no code with the QRCoder renderer, and SPAYD payloads are checked against the published example with a separate reader.

## License

Pairrot is licensed under the [Elastic License 2.0](LICENSE) (ELv2).

You may use, copy, modify, and distribute Pairrot, and integrate it into your own products, including commercial ones. You may not offer Pairrot itself to third parties as a hosted or managed service — for example, running it as a payment-pairing (QR pairing) SaaS — where users get access to a substantial set of its features. You may not remove or obscure the licensing and copyright notices.
