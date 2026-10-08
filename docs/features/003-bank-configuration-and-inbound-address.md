# Feature 003: bank configuration and an inbound email address

Changed by [Feature 013](./013-bank-account-identity-and-reuse.md) (implemented): an account is exclusive only among active registrations. Archiving or deleting the tenant releases it, an archived connection can be restored, and variable symbols and transaction identity are kept per physical account.

## Goal

Let a user configure a tenant's bank account and request an email address intended for bank payment notifications.

For this feature, Resend is replaced by a simple local stub. Pairrot saves the bank details and generates a test address, but does not receive real emails or process payments.

The feature builds on [tenant ownership and API keys](./001-tenants.md) and [user login and sessions](./002-user-login-and-sessions.md). It uses local PostgreSQL in Docker and the local Vite + React SPA. Root tenants and subtenants use the same bank configuration and behavior.

## 1. User flow

1. The user signs in and selects a tenant they administer.
2. They open Bank settings and add a bank account, with an optional label such as “Main account”.
3. Pairrot validates and saves the account details.
4. The user chooses **Create notification address**.
5. Pairrot uses the local email-provider stub to generate and save an address for that bank connection.
6. The user can view and copy the saved account details and address after refreshing the page or signing in again.

The screen explicitly says: **“Test address — real bank emails are not received yet.”** It must not instruct the user to configure this test address in live internet banking or show a successful bank connection.

## 2. Bank connections belong to tenants

Use a bank connection record belonging to one tenant. Do not put a single bank-account field directly on the user or make every child share NaLekci's account.

- A tenant can have more than one bank connection, each with its own bank details and notification address.
- A root tenant can configure its own account in exactly the same way as a subtenant.
- Each connection belongs to one tenant for its entire lifetime.
- A notification address belongs to one connection and therefore one tenant.
- Bank configuration grants no new permissions and does not change tenant capabilities.
- `can_create_campaigns` remains `false`. Campaigns are outside this feature.

The existing tenant name and contact email remain editable through the configuration endpoint from Feature 001. The contact email is a human contact address. The generated notification address is a separate address intended to receive bank messages later. Neither is a user's login identity.

## 3. Bank details

Feature 003 initially supports Czech bank accounts and CZK. This is a proposed scope limit for the first implementation; other countries and currencies can be added separately.

| Field | Meaning |
| --- | --- |
| `id` | Stable bank connection identifier assigned by Pairrot. |
| `tenant_id` | Owner, assigned from the authorized request context. |
| `label` | Optional user-facing name for the connection. |
| `account_number` | Czech account number, including optional prefix and bank code. |
| `iban` | Normalized Czech IBAN identifying the same account. |
| `currency` | `CZK` in this feature. |
| `status` | `active` or `archived`; describes whether the configuration is in use, not whether email reception works. |
| `created_at`, `updated_at`, `archived_at` | Record lifecycle timestamps. |

The API accepts either a domestic account number or a Czech IBAN. The backend validates the supplied format and check digits and derives the other representation. If both are supplied, they must identify the same account. Reject unsupported countries, unsupported currencies, and inconsistent or invalid account details.

Store normalized values so whitespace or equivalent domestic and IBAN forms cannot create duplicate connections. A valid format does not prove account ownership or that the account exists; the response and UI must not label it as bank-verified.

Within a tenant, the same normalized account can have only one active connection. Attempts to create another return a conflict. For this initial version, an account already assigned to another tenant is also rejected with a generic account-unavailable error that does not reveal the other tenant. This carries forward the MVP rule that different recipients do not share one bank account.

### Changes and archiving

- The label can be edited.
- The saved bank account, currency, and tenant owner are immutable. To use another account, create a new connection and archive the old one.
- Archiving preserves bank details, the assigned address, and history. It does not delete the tenant.
- Archived connections cannot generate a new address. Their existing addresses remain reserved and must never be assigned to another connection.
- Repeating an archive operation is safe. Restoring or reusing an archived bank connection is deferred; for now its normalized account remains reserved.

Keeping the account identity stable prevents a future bank email from being interpreted against different account details after an edit.

## 4. Notification address

The user requests an address only after the bank connection is saved. Pairrot chooses the address; the user does not enter a mailbox name or their personal email.

| Field | Meaning |
| --- | --- |
| `inbound_address` | Generated address; `null` until requested. |
| `email_provider` | `stub` in this feature. |
| `address_status` | `not_created` or `stub_created`. |
| `address_created_at` | When the address was first assigned; `null` before that. |

Requirements:

- Generate an opaque unique address, for example `p_<random-value>@in.pairrot.test`.
- Use an explicitly configured local test domain. Do not use a real customer's domain or require DNS setup.
- The address must not include a person's name, login email, bank number, or sequential tenant ID.
- Return the same saved address on repeated requests. A retry or double click must not allocate another address.
- Concurrent requests must result in one saved address for the connection.
- Keep the address across application and database container restarts when the database volume is retained.
- Show the address in later reads; unlike an API key, it is not a one-time secret or an authentication credential.
- For an archived connection, return the saved address as historical configuration, with the archived state clearly visible.

`stub_created` means only that a local test address has been assigned. There is no `receiving`, `verified`, or `connected` state in this feature because no bank email has been received.

## 5. The Resend stub

Use a small backend abstraction for assigning notification addresses, with a local stub implementation selected by application configuration. The frontend calls Pairrot's API; it does not know provider credentials or call Resend directly.

The stub does only the following:

1. Supplies a test address for a saved bank connection when one has not yet been assigned.
2. Returns the already assigned address on subsequent requests.
3. Reports that it is running in stub mode.

Pairrot persists the assignment in PostgreSQL. In-memory stub state must not be the only place an address exists. Assignment, uniqueness checks, and the audit record must succeed together; a failed operation leaves a retryable connection without a partially assigned address.

This is an application-level substitute for the future email integration, not an emulation of Resend's complete API. It needs no Resend account, token, network call, domain registration, or worker. Do not add a fake email inbox, simulated delivery, inbound webhook, message parser, or queue as part of this feature.

A later feature will define actual email routing and receipt through Resend. The test addresses are not production-ready and must not silently change to “receiving” when a real provider is introduced.

## 6. API additions and permissions

### Current tenant

These endpoints accept either a tenant API key from Feature 001 or an administrator session for the selected tenant from Feature 002.

| Method and path | Behavior |
| --- | --- |
| `GET /v1/tenant/bank-connections` | List the tenant's connections; active by default, with an option to include archived records. |
| `POST /v1/tenant/bank-connections` | Create a connection from account details and an optional label. Return the saved record with `address_status = not_created`. |
| `GET /v1/tenant/bank-connections/{id}` | Return bank details, lifecycle state, provider mode, and notification address state. |
| `PATCH /v1/tenant/bank-connections/{id}` | Update the label of an active connection. Reject attempts to change immutable or provider-managed fields. |
| `DELETE /v1/tenant/bank-connections/{id}` | Archive the connection. |
| `POST /v1/tenant/bank-connections/{id}/inbound-address` | Assign a test address or return the existing assignment for an active connection. |

The current-tenant endpoints do not accept a caller-supplied `tenant_id` as ownership. Existing bearer-versus-cookie rules, session checks, antiforgery protection, and expected-tenant checks remain in force.

### Parent management

Expose the same operations under `/v1/subtenants/{subtenantId}/bank-connections`, including the connection detail and `/inbound-address` suffixes. This lets NaLekci configure accounts on behalf of its instructors and studios without pretending to be logged in as those users.

These routes require either NaLekci's management key or an authorized parent administrator session, with `can_create_subtenants = true`. Access is limited to direct children. Check both the child ownership and the requested bank connection's owner on every operation.

Suspended children may be inspected by the authorized parent, but changes require an active child. Deleted children and their bank connections are retained for history and cannot be changed. Suspension or deletion of the parent blocks its children's own access, as in Feature 001.

### Response and error behavior

- `201` when creating a bank connection.
- `200` for reads, label updates, and address assignment. Returning an already assigned address has the same result shape.
- `204` for archiving, including repeated archiving.
- `400` for invalid input, mismatched account representations, or unsupported fields, countries, or currencies.
- `401` or `403` for authentication and permission failures, following the existing features.
- `404` for missing connections or connections outside the caller's permitted scope.
- `409` for an unavailable account, an incompatible lifecycle state, or a changed browser tenant context.

No response may reveal another tenant's details. A saved connection is not usable as a way to discover foreign tenant data through error messages.

## 7. Data additions

Add a `bank_connections` table with the bank detail and address fields above and a foreign key to `tenants`. A separate bank account entity is unnecessary for this feature.

Required database rules:

- Unique normalized IBAN across connections, including archived connections under the initial no-reuse rule.
- Unique non-null inbound address across all tenants and connections.
- `inbound_address` and `address_created_at` are both empty for `not_created`, and both set for `stub_created`.
- `archived_at` is set exactly when status is `archived`.
- A connection always belongs to an existing tenant. Tenant deletion preserves the connection for history and prevents further changes.

Record connection creation, label changes, address assignment, and archiving in the existing tenant audit history. Include the connection ID and the actual user or API-key actor. Repeating address creation or archiving without a state change must not create another assignment event.

Account validation, tenant authorization, and provider behavior belong in the backend. The SPA may help with input formatting, but it must not be the only place these rules run.

## 8. Local test UI

Add a Bank settings section for the selected tenant:

- List saved connections, their labels, bank details, and archived state.
- Add an account using a domestic number or IBAN.
- Show validation errors next to the relevant inputs.
- Edit a label and archive a connection.
- Create, view, and copy the connection's notification address.
- Clearly display stub mode and that email reception is not yet available.

For users allowed to manage direct children, provide the same bank settings inside the selected child's detail. Keep the owner visible so a parent administrator can distinguish their own account from a studio's account.

The SPA remains local only. A successful address assignment is a testable configuration result, not evidence of a working email connection.

## 9. Acceptance criteria

1. A NaLekci administrator and an instructor administrator can each save their own bank connection using the same functions.
2. Domestic account input and the equivalent Czech IBAN produce the same normalized account identity. Invalid checksums, conflicting representations, unsupported countries, and unsupported currencies are rejected.
3. Duplicate creation does not produce another connection, including when requests arrive concurrently. A conflict involving another tenant reveals no owner information.
4. An authorized parent can configure a direct child's bank connection. A sibling, unrelated tenant, or incorrect key purpose cannot read or change it.
5. Before address creation, the response has no address and reports `not_created`.
6. Address creation returns a unique test address and `stub_created`. Repeated and concurrent requests leave exactly one address assigned to that connection.
7. Reloading the SPA or restarting the application preserves the bank details and assigned address.
8. The UI shows that the address is a stub and does not claim that bank notifications are being received.
9. A tenant's contact email and a user's login email remain unchanged when an inbound address is assigned.
10. Changing account identity or tenant ownership is rejected. Label updates succeed and are recorded in the audit history.
11. Archiving preserves the address and history, blocks new address assignment, and never makes the address available to another tenant.
12. Suspended or deleted tenant rules, browser session checks, and antiforgery protection apply to the new operations.
13. The complete flow runs against local PostgreSQL with the stub, without Resend credentials, DNS configuration, or outbound provider calls.

## 10. Deferred

Real Resend integration, domain setup, email receipt, inbound webhook verification, message storage, bank-specific parsers, account ownership verification, payment requests, QR generation, matching, campaigns, and production frontend deployment belong to later features.

The result of Feature 003 is a configured tenant bank connection with a stable test notification address, ready for a later feature to add real email reception.

[Feature 004](./004-payment-qr-generation.md) adds payment QR generation for a saved bank account. It works independently of notification address creation and email reception.
