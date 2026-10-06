# Feature 004: payment QR generation

## Goal

Generate a Czech payment QR code for a tenant's saved bank account, with an amount and optional payment identifiers. The integrating application receives payment data; the local test SPA can display and download the QR image.

This feature builds on [bank configuration](./003-bank-configuration-and-inbound-address.md) and the access rules from Features [001](./001-tenants.md) and [002](./002-user-login-and-sessions.md). Root tenants and subtenants use the same QR functions.

The proposed scope is generation only. Generating a QR code does not create an expected-payment record, reserve a variable symbol, initiate a transfer, or mark anything as paid. Those functions will be specified separately.

## 1. User flow

1. The user signs in and selects a tenant.
2. They select an active bank connection and open Generate payment QR.
3. They enter an amount and, optionally, a variable symbol and a message for the recipient.
4. Pairrot validates the request and generates payment data and a QR image.
5. The SPA displays the QR alongside the receiving account, amount, currency, variable symbol, and message.
6. The user can copy the payment data and download the QR as a PNG image.

NaLekci can perform the same operation through the API without a browser session, using the appropriate key.

The bank connection does not need an inbound email address. QR generation works whether its email state is `not_created` or `stub_created`; it does not call the Resend stub.

## 2. Payment inputs

Use the selected connection's stored, normalized bank details. Do not let the caller replace the receiving account in the QR request.

| Input | Required | Pairrot rule |
| --- | --- | --- |
| Bank connection ID | Yes, in the endpoint path. | Must belong to the permitted tenant and be active. |
| `amount` | Yes. | Integer in minor units: `45000` means CZK 450.00. Allowed range: 1 to 999999999 minor units. |
| `currency` | Yes. | `CZK`, matching the saved connection. No currency conversion. |
| `variable_symbol` | No. | A string containing 1–10 ASCII digits. Keep leading zeros in the generated data. Omit when not supplied. |
| `message` | No. | Optional recipient message. Reject content that cannot be represented within the chosen payment format limits. |

These input requirements are Pairrot's selected subset of QR Platba, not a claim that every field is mandatory in the underlying standard.

Reject zero or negative amounts, fractional minor units, unsupported currencies, unknown fields, and invalid symbols. The browser converts the entered decimal amount into minor units exactly; the backend independently validates it. Never use binary floating-point arithmetic for money.

Treat an omitted or `null` optional field as absent. An empty variable symbol is invalid. Trim surrounding whitespace from the message; omit it if it becomes empty. Do not shorten a message, drop its accents, or change its content to make it fit without reporting the problem.

## 3. Payment format

Use the Czech QR Platba / SPAYD format. The payload begins with `SPD*1.0*`; map IBAN to `ACC`, the decimal amount to `AM`, currency to `CC`, the variable symbol to `X-VS`, and the recipient message to `MSG`. Format the amount with a decimal point and two digits after it. The standard limits `AM` to 9,999,999.99, `X-VS` to ten digits, and `MSG` to 60 characters. Follow its value-encoding rules for special characters. [Official QR Platba specification](https://qr-platba.cz/pro-vyvojare/specifikace-formatu/).

For predictable validation, Feature 004 conservatively requires the encoded message value to fit within the 60-character field limit. Return a field-specific error when it does not fit, rather than producing a truncated payment message. Encode literal percent signs, delimiters, and non-ASCII text so decoding reproduces the intended message and cannot introduce another payment field.

Use one deterministic attribute order: `ACC`, `AM`, `CC`, then optional `X-VS` and `MSG`. Identical validated inputs and bank details must produce the same payload. Do not include an API key, login identity, tenant ID, notification address, or other internal data in the code.

The backend constructs the payload from validated fields. Do not accept an arbitrary caller-supplied QR payload as an alternative input.

## 4. Results

### Payment data

The JSON result contains:

- The bank connection ID.
- Domestic account number and IBAN from the saved connection.
- `amount` in minor units, `currency`, and the effective optional fields after validation.
- QR format name `SPAYD` and the complete `payload` string.

The displayed payment summary and rendered QR must come from these same validated values. A caller may render the returned payload in its own application; Pairrot also provides a PNG endpoint.

### QR image

- Generate the PNG in the backend using the same payload builder as the JSON endpoint.
- Return `image/png` with a filename suitable for download.
- Use a black code on an opaque white background, a clear border around the code, and integer-sized modules without blur or smoothing.
- Use error correction level M as recommended by the payment-format specification. [QR Platba specification](https://qr-platba.cz/pro-vyvojare/specifikace-formatu/).
- Choose a fixed rendering policy that scales to the encoded content. Custom colors, logos, image dimensions, and SVG are outside the initial scope.

PNG bytes do not have to remain identical across renderer upgrades, but decoding must return the same payment payload for the same inputs.

Generating and downloading a code happens within Pairrot. Do not use an external QR generation service or upload account details and payment messages to a third party.

## 5. API additions

### Current tenant

| Method and path | Result |
| --- | --- |
| `POST /v1/tenant/bank-connections/{id}/payment-qr` | JSON payment data and SPAYD payload. |
| `POST /v1/tenant/bank-connections/{id}/payment-qr.png` | PNG QR image for the same request fields. |

Example request body:

```json
{
  "amount": 45000,
  "currency": "CZK",
  "variable_symbol": "0000123456",
  "message": "Yoga October"
}
```

Both endpoints use the same validation and permission checks. Neither creates a saved payment resource, so success returns `200`, not `201`.

### Parent management

Expose the same two operations under `/v1/subtenants/{subtenantId}/bank-connections/{id}/payment-qr` and `/payment-qr.png`, following the explicit child-management pattern in Feature 003.

This allows NaLekci to generate a code for a direct child's saved account using its management key. A root tenant's own account is accessed through the current-tenant endpoints using its tenant key or an authorized user session.

### Access rules

- Current-tenant endpoints require the owning tenant's key or an administrator session with that tenant selected.
- Child-management endpoints require the parent's management key or an authorized parent administrator session, plus `can_create_subtenants = true`.
- Validate both the child relationship and ownership of the requested bank connection. A connection ID must not bypass tenant isolation.
- The recipient tenant and any parent must be active. Archived bank connections cannot generate new QR codes.
- Existing session expiry, antiforgery, and expected-tenant checks apply to browser requests. Existing bearer-key authentication behavior remains unchanged.
- `can_create_campaigns` remains `false`; no campaign permission is needed for a payment QR.

### Errors

Return readable JSON errors, including from the PNG endpoint: `400` for invalid payment input, `401` or `403` for authentication or permission failures, `404` for a missing or foreign connection, and `409` for an archived connection or changed browser tenant context.

The SPA must inspect the response status and content type before treating a response as an image. Clear an old preview when generation fails or its inputs change so it cannot be mistaken for the new payment.

## 6. Storage and repeated requests

No new database table is required. Read the bank connection and return generated data or image bytes. Do not persist PNG files or introduce a payment-request lifecycle in this feature.

Repeated calls are safe and do not create duplicate business records. A variable symbol can be used again because this operation does not reserve symbols; the later expected-payment feature will define uniqueness and matching rules.

Do not add a business audit event claiming a payment or bank transfer was created. Existing request logs may record the operation outcome and connection ID, but should not log the full payload, message, or image. Responses should not be stored in shared caches.

## 7. Local SPA

Add a Generate payment QR action to active bank connections in the selected tenant and in permitted child details.

The form provides an amount, optional variable symbol, and optional recipient message. Show the destination account as read-only and make the tenant owner visible. Currency is fixed to CZK for this feature.

After generation, show the QR, a readable payment summary, a copy-payload action, and a download-PNG action. Render message text as plain text. Keep the preview tied to the submitted values; editing the form, switching tenant, or changing the selected connection clears it until the user generates a new result.

The local UI should explain that the QR contains payment instructions and that Pairrot is not yet tracking whether the payment arrives. A code for a real bank account may prefill an actual payment in a banking app; scanning it does not mean Pairrot has confirmed a payment.

## 8. Acceptance criteria

1. A tenant administrator can generate and download a payment QR for an active saved account. A tenant-key client can obtain the same result through the API.
2. NaLekci can generate a QR for a permitted direct child. Foreign tenants and siblings cannot use that child's bank connection.
3. The result uses the saved recipient account. Attempts to override IBAN, account number, or tenant ownership in the request fail.
4. The image decodes to exactly the JSON payload. An independent decoder or validator confirms the account, amount, currency, variable symbol, and message.
5. Amounts at the lower and upper bounds work; zero, negative, fractional minor units, and values above the limit fail. CZK 450.00 is never encoded as CZK 45,000.00.
6. Leading zeros in the submitted variable symbol remain in the payload. Omitted symbols work, while invalid symbols fail.
7. Encodable Czech text and special characters round-trip correctly. A message containing a delimiter cannot add or replace payment fields. Oversized messages produce a clear error without truncation.
8. Repeating generation produces the same payload and creates no payment, transaction, or extra bank record.
9. Archived connections and suspended or deleted tenants cannot generate new codes. Existing browser and key access checks still apply.
10. The code works without a generated inbound address and without calling Resend or its stub.
11. The local SPA displays and downloads the correct image and clears stale previews after edits, tenant changes, or errors.
12. Manually scan a generated sample in at least one Czech banking application and check its prefilled fields without submitting a transfer. Record the tested app and result; do not infer support for every bank from one check.
13. The feature runs locally with the existing database and SPA, without an external QR service or a production frontend.

Use independent decoding and published valid examples to check the generated data. Do not validate only by comparing a renderer with its own encoder. The manual banking-app check is a release verification step, not an instruction to send money.

## 9. Deferred

Persisted payment requests, external booking references, automatic variable-symbol assignment, symbol reservation, due dates, additional payment symbols, recurring payments, public payment pages, PDFs, incoming emails, payment matching, payment-status webhooks, campaigns, and production frontend deployment remain separate features.

The output of Feature 004 is a usable payment payload and QR image for a configured bank account.
