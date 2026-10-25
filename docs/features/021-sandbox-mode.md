# Feature 021: sandbox mode for integration testing

Status: proposed. Local only: sandbox mode runs on a developer's machine against the local database, never on the VPS.

## Goal and scope

An application that integrates Pairrot (NaLekci first) needs to test its whole flow: create a payment request, show the QR code, *the payer pays*, receive `payment_request.updated` and the Feature 016 events, and react. Today the only way to make a payment arrive is a real bank notification e-mail, so integrators cannot test the payment side at all.

Sandbox mode lets a developer simulate incoming payments over the API. A simulated payment is not a shortcut around the pipeline: it is rendered as a bank notification and handed to the real inbound intake, so parsing, transactions, matching, review cases, payment-change journal, and webhooks run exactly as for a real e-mail. Everything it produces is **test** data and stays test data.

Out of scope: a hosted/public sandbox, separate sandbox databases or tenants flagged as sandbox, time travel, simulating webhook receivers, simulating Resend.

## 1. Enabling

- New configuration section `Sandbox`: `Enabled` (`false`).
- `Sandbox:Enabled = true` is allowed only in the `Development` and `Testing` host environments. Otherwise the API (and the CLI) refuses to start with `Sandbox:Enabled is only allowed in Development and Testing; sandbox payments are never real.` (same pattern as `Matching:AllowTestEvidence`, `PaymentLedger.cs`).
- Sandbox mode requires test evidence to be matchable: when `Sandbox:Enabled` is true, `Matching:AllowTestEvidence` is treated as true (the effective value used by `PaymentLedger.AllowTestEvidence`). Nothing else about matching changes.
- When sandbox mode is off, no sandbox route is mapped (`404` like any unknown route) and the sandbox bank profile is not registered.
- `appsettings.Development.json` keeps `Sandbox:Enabled = false`; the README shows `Sandbox__Enabled=true` (and the `launchSettings` profile `https-sandbox`, see section 7).

## 2. Data boundaries

- Simulated notifications enter through the connection's **active inbound address**, which must belong to an instance with `Environment = test` (the built-in `stub`). If the connection has no address yet, the simulation assigns one through the existing `inbound-address` service (idempotent). If its active address is on a live instance: `409 sandbox_requires_test_address`.
- The resulting message therefore has `environment = test`, its transaction has evidence `test`, and every payment change and event is `environment = test`. Existing Feature 010/016 rules then deliver them only to **test** webhook endpoints. Nothing new is needed for that, but tests must prove it (a live endpoint receives nothing).
- `source_kind` stays whatever the instance says (`received` for the stub). Delivery evidence is `sandbox`, transport kind `sandbox`, so `inbound show` and the source make the origin obvious.
- Archived connections, inactive tenants, paused addresses: the simulation goes through intake unchanged, so the normal hold/pause rules apply. The API does not pre-check them except archived connection → `409 bank_connection_archived` (no point in creating held mail on purpose).

## 3. The sandbox bank profile and rendering

The parser must recognize a simulated notice. Two renderers, chosen by the destination account's bank code:

1. **Bank code with a real profile (0800 Česká spořitelna):** render an HTML notification in the real George template (the same structure as `tests/Fixtures/ceska-sporitelna/incoming-transfer-with-vs.html`), From `…@csas.cz`, with the receiving-system authentication evidence `dmarc=pass` supplied by the sandbox adapter. The real `CeskaSporitelnaProfile` parses it. This keeps the sandbox honest about what the real parser accepts.
2. **Any other bank code:** a sandbox profile `pairrot_sandbox` (version `1.0.0`) in `Pairrot.Core.Parsing`, registered **only in sandbox mode**, as the fallback of `BankProfileRegistry` for bank codes that have no real profile. Trusted sender: `notifikace@banka.sandbox.pairrot.test` with `dmarc=pass`. Plain-text template with labeled lines (direction, account, amount, booking date and time, VS, message, counterparty account and name, bank reference). It supports a bank reference, so duplicates and disputes can be tested.

A renderer and its profile live next to each other (`ISandboxNoticeRenderer` keyed by profile ID). A unit test round-trips every renderer through its profile for every supported field combination.

## 4. API

Both routes exist under the tenant group and the subtenant group, with the same authorization as `POST .../bank-connections/{id}/inbound-address` (tenant key, management key for direct children, browser session with the tenant header and antiforgery).

### 4.1 `POST .../bank-connections/{id}/sandbox/incoming-payments`

```json
{
  "amount": 45000,
  "variable_symbol": "2026000001",
  "message": "Lekce 12. 11.",
  "payer_name": "Jana Nováková",
  "payer_account": "19-2000145399/0100",
  "booked_at": "2026-10-25T09:15:00+01:00",
  "bank_reference": "SBX-0001",
  "direction": "incoming"
}
```

| Field | Rules |
| --- | --- |
| `amount` | Required, minor units, 1 … 99 999 999 999 (same range as payment requests). |
| `variable_symbol` | Optional, 1–10 digits. |
| `message` | Optional, ≤ 140 chars. |
| `payer_name`, `payer_account` | Optional; defaults `Sandbox Plátce`, `19-2000145399/0100`. `payer_account` must be a valid Czech account. |
| `booked_at` | Optional ISO time with offset, default now (Prague time on the notice). |
| `bank_reference` | Optional, only for the sandbox profile; for a bank whose real profile has no reference: `400 field_not_supported_by_bank`. |
| `direction` | `incoming` (default) or `outgoing` (renders a debit, which parses as `not_payment`). |

Unknown fields → `400` like other endpoints. The request accepts `Idempotency-Key` only in the sense that the rendered delivery ID is derived from it when given (`sandbox:<tenant>:<key>`), so a retried request creates one message; without the header every call is a new notification (deliberately, so "the bank sent it twice" can be simulated).

Response `202`:

```json
{ "inbound_message_id": "…", "bank_connection_id": "…", "profile": "pairrot_sandbox", "status": "accepted" }
```

Processing continues in the background jobs (woken immediately by the work signal). The client follows the result through existing endpoints (`GET .../inbound-messages/{id}`, transactions, payment requests) or webhooks.

### 4.2 `POST .../payment-requests/{id}/sandbox/pay`

Convenience for the common case. Body (all optional): `{ "amount": 45000, "variable_symbol": "…", "booked_at": "…", "payer_name": "…" }`. Defaults: `amount` = the request's unpaid remainder (`amount - paid_amount`, at least 1), `variable_symbol` = the request's symbol, connection = the request's connection. Cancelled request → still allowed (tests a payment of a cancelled request). Fully paid request without `amount` → `409 payment_request_already_paid`. Same `202` response plus `payment_request_id`.

### 4.3 `GET /v1/sandbox`

Unauthenticated-safe status for the SPA and integrators: `{ "enabled": true, "profiles": ["ceska_sporitelna", "pairrot_sandbox"] }`. Mapped only when enabled, so `404` means off.

### 4.4 Audit

Each simulation writes a tenant audit event `sandbox.payment_simulated` (connection, message ID, amount, VS; no names).

## 5. Admin CLI: `sandbox seed`

```bash
dotnet run --project src/Pairrot.Admin -- sandbox seed [--root <external-id>] [--webhook-url <url>] [--bank-code <code>]
```

Refuses unless `Sandbox:Enabled` (same environment check). Idempotent, for a fresh local database:

1. Ensures a root tenant (default external ID `sandbox.local`, name `Sandbox`) through the same code as `setup` (without creating an administrator).
2. Issues a new tenant key and management key and prints them once (like `keys issue`).
3. Ensures one active bank connection with a generated valid account (default bank code `0800`; any other code gets the sandbox profile) and its stub inbound address.
4. With `--webhook-url`: ensures a **test** webhook endpoint (`scope own_and_direct_children`, all event types) to that URL and prints its signing secret once. The URL must pass the normal destination rules; a local receiver needs `Webhooks:AllowedLocalDestinations`. The command prints that hint when the URL is refused.
5. Prints a ready-to-run `curl` for creating a payment request and for `sandbox/pay`.

## 6. Local SPA

When `GET /v1/sandbox` returns enabled, the SPA shows a "Sandbox" badge in the header, a **Simulate payment** action on each payment request row (`ExpectedPayments.tsx`; opens a small form prefilled with the remainder and VS, posts to `sandbox/pay`), and a **Simulate incoming payment** form in `BankSettings.tsx` per connection with all fields of 4.1. After submitting it shows the message ID and links to the inbound message.

## 7. Developer workflow

`Properties/launchSettings.json` of the API gets an `https-sandbox` profile (`ASPNETCORE_ENVIRONMENT=Development`, `Sandbox__Enabled=true`). README gets a "Sandbox mode" section: start DB, `migrate`, `sandbox seed --webhook-url http://localhost:5099/pairrot`, run with `--launch-profile https-sandbox`, create a payment request, `sandbox/pay`, watch the webhook.

## 8. Tests

Integration (Testcontainers, `Sandbox:Enabled = true` in a dedicated fixture or per-test host configuration):

- Exact `sandbox/pay` → request `paid`, allocation exists, `payment_request.updated` and `transaction.recorded` delivered to a test endpoint with `environment = test`; a live endpoint of the same tenant receives nothing.
- Partial, then remainder → `partially_paid` then `paid`; overpay → `overpaid`; wrong VS → unmatched transaction on the attention list; `outgoing` → `not_payment`, no transaction.
- Sandbox profile with the same `bank_reference` twice → one transaction (duplicate link); same reference with a different amount → disputed.
- Česká spořitelna connection → parsed by `ceska_sporitelna`; `bank_reference` there → `400 field_not_supported_by_bank`.
- Subtenant route with a management key; another tenant's connection → `404`.
- Live-instance address → `409 sandbox_requires_test_address`; archived connection → `409`.
- `Idempotency-Key` repeated → one message.
- Sandbox off → routes `404`, sandbox profile not registered (a message from the sandbox sender to a non-0800 account is `unsupported`).
- `Sandbox:Enabled` in `Production` → startup fails with the message from section 1.

Unit: renderer ↔ profile round-trips, sandbox profile trust (wrong sender, missing DMARC → untrusted), field validation.
