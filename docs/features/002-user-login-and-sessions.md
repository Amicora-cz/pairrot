# Feature 002: user login, sessions, and API key management

## Goal

Let a person sign in with an email and password, open a tenant they administer, and generate or revoke its API keys through the local test application.

This feature builds on [Feature 001](./001-tenants.md). It adds human access alongside the API keys already used by integrations. It uses local PostgreSQL in Docker and the local Vite + React SPA. No production frontend, Resend, or email delivery is required.

## 1. Who generates API keys?

**The Pairrot backend generates every API key.** A person or application requests a key; it does not choose or calculate the secret.

| Stage | How keys are issued |
| --- | --- |
| Feature 001: initial setup | An administrative command creates NaLekci and its initial tenant and management keys. No browser login is needed. |
| Feature 001: subtenant creation | NaLekci calls Pairrot using its management key. Pairrot creates the child and returns its first tenant key once. |
| Feature 002: manual management | A signed-in tenant administrator requests a key in the local SPA. Pairrot checks permissions, generates the key, and returns its secret once. |

Feature 001 can be completed and used independently. Existing keys continue to work when Feature 002 is added; users are not required to sign in before every integration call.

## 2. A user and a tenant are different things

- A **user** is a person who signs in with an email and password.
- A **tenant** owns configuration, keys, and later payment data.
- A **membership** gives a user access to a tenant.
- An **API key** belongs to a tenant and is used by an application, such as the NaLekci backend.
- A **session** represents a user's authenticated browser access.

Feature 002 has one membership role: `admin`. A user may administer more than one tenant, and a tenant may have more than one administrator. Fine-grained roles are deferred.

User login emails are unique after normalization. A tenant's `contact_email` remains separate: changing it does not change a user's login, create a user, or grant access. Tenant contact emails may still be shared as described in Feature 001.

Membership in NaLekci permits management of its direct children when `can_create_subtenants` is enabled. It does not automatically create memberships in those children. A child administrator cannot access its parent or siblings.

## 3. Initial user setup

Extend the administrative setup tooling with explicit operations to:

- Create a user with a supplied login email and password.
- Add or remove an `admin` membership for an existing tenant.
- Disable a user and revoke their sessions.
- Reset a user's password when they cannot sign in.

The first user is assigned to `nalekci.cz`. Repeating setup does not reset their password or duplicate the user or membership. Credentials are supplied securely during setup and are not committed to the repository or printed in logs.

The same tooling can create a user for a studio or instructor for local testing. Creating a subtenant through the management API does not automatically create a login account. There is no public signup, invitation flow, or emailed password reset in this feature.

## 4. Sign-in and sessions

### Sign-in flow

1. The user opens the local SPA and enters their login email and password.
2. Pairrot validates the credentials and creates a new browser session.
3. The browser receives an authentication cookie. The response provides user details and accessible tenant memberships, never the password or cookie secret.
4. With one accessible tenant, that tenant is selected automatically. With several, the user selects one before performing tenant operations.
5. The SPA shows the selected tenant and its allowed actions.

Use ASP.NET Core Identity for user credentials and password verification, with ASP.NET Core cookie authentication for browser access. No custom password hashing is needed. Keep membership and tenant authorization in Pairrot's domain model.

Failed sign-in returns a generic invalid-credentials response. Add configurable login throttling and temporary lockout for repeated failures. Disabled users cannot sign in.

### Session behavior

The proposed first-version defaults are:

| Setting | Behavior |
| --- | --- |
| Authentication | A protected, `HttpOnly` cookie; no authentication token in browser local storage. |
| Server-side record | PostgreSQL stores the user, selected tenant, creation and expiry times, and revocation state for each session. |
| Idle expiry | 30 minutes without authenticated activity. Background UI polling does not keep a session alive indefinitely. |
| Absolute expiry | 8 hours after login, even if the user remains active. |
| Sign-out | Revoke the current session in PostgreSQL and clear the cookie. A copied old cookie must stop working. |
| Sign out everywhere | Revoke all sessions for the current user. |
| Password change or administrative reset | Revoke all sessions and require a new login. |
| User disabled | Reject existing sessions on their next request. |
| Membership removed | Immediately reject access to that tenant, including from an already selected session. |

These durations are application configuration, not fixed tenant settings. No “remember me” option or refresh-token endpoint is needed for this feature.

The cookie identifies a server-validated session. Check session validity and current permissions on every protected request; do not rely on a role or capability copied into a cookie at login. A newly authenticated session must not reuse an anonymous session identifier.

If the selected tenant or its parent is suspended or deleted, block operations for that tenant. A user who has another active membership can select it. A user with no usable memberships may sign out or manage their own password, but cannot operate on tenant data.

Session records and the ASP.NET Core cookie-protection keys must survive ordinary application restarts. PostgreSQL remains the source of session revocation and expiry. The cookie itself must not be stored in the database as a reusable credential.

### Local browser setup

Use the Vite development proxy for API calls, so the SPA calls relative `/v1/...` paths through its own origin. Use local HTTPS and a cookie configured with `Secure`, `HttpOnly`, `SameSite=Lax`, and no shared-domain scope.

Cookie-authenticated state changes require ASP.NET Core antiforgery protection, including login, logout, tenant selection, configuration changes, and key issuance. The SPA obtains an antiforgery token from the API and sends it in a request header. Refresh it after login and logout. Do not depend on `SameSite` alone.

The SPA remains a local test application. These login screens do not introduce a production frontend deployment.

## 5. What a signed-in administrator can do

For the selected tenant, an administrator can:

- Read and update the tenant's name and contact email.
- List API key metadata, create a tenant key, and revoke a key.
- Create a management key only when the tenant is a root tenant with `can_create_subtenants = true` under the hierarchy rules of Feature 001.
- Use the existing direct-child CRUD and child key-management operations when the selected tenant has that capability.
- Read existing change history for the selected tenant and its permitted direct children.

The same current-state checks apply to browser operations and API-key operations. A session cannot grant capabilities, move a child, create grandchildren, or reach an unrelated tenant. `can_create_campaigns` remains `false` for all tenants.

The parent administrator is explicitly trusted to manage its children's configuration and issue child keys, as in Feature 001. They do not silently become a member of the child. Direct use of that child's current-tenant endpoints requires a child membership or its tenant key.

### Issuing a key in the browser

1. The administrator selects the tenant and chooses the key purpose and an optional label.
2. Pairrot checks membership, tenant status, and capabilities.
3. Pairrot generates and stores the key using the same key service as Feature 001.
4. The SPA displays the secret once for copying into the integrating application's configuration.
5. Later visits show metadata only. A lost secret is replaced by issuing another key and revoking the old one.

The browser session itself grants access to this screen; the user does not need to paste a management key to generate one. The SPA must not store issued keys in local storage or silently reuse them as its own login. Clear the one-time secret view when it is dismissed, the tenant is changed, or the user signs out.

Tenant keys remain independent of browser sessions. Signing out, changing a user's password, or removing a membership does not silently stop tenant integrations by revoking all their keys. If a key may be compromised, an authorized administrator explicitly revokes it. Tenant suspension and deletion still block keys as defined in Feature 001.

## 6. API additions

### Authentication and session endpoints

| Method and path | Purpose |
| --- | --- |
| `GET /v1/auth/antiforgery` | Obtain an antiforgery token for browser requests, including before login. Does not create an authenticated session. |
| `POST /v1/auth/login` | Validate `email` and `password`, create a session, and set the cookie. |
| `GET /v1/auth/session` | Return the signed-in user, current memberships, selected tenant, and session expiry information. |
| `POST /v1/auth/select-tenant` | Select a tenant only after checking the user's active direct membership. |
| `POST /v1/auth/logout` | Revoke the current session and clear its cookie. |
| `POST /v1/auth/logout-all` | Revoke every session for the signed-in user and clear the current cookie. |
| `POST /v1/auth/change-password` | Verify the current password, save the new password, and revoke all user sessions. |

Tenant selection is session state and affects all browser tabs sharing that session. For tenant-scoped browser operations, the SPA also sends its expected tenant ID. Pairrot rejects a mismatch with `409 tenant_context_changed`; the supplied ID is a consistency check, never authority to access a tenant. This prevents a stale tab from changing a different tenant after another tab switches context.

### Current-tenant key management

These new endpoints require a user session and `admin` membership in the selected tenant. An ordinary tenant key cannot issue more keys for itself.

| Method and path | Purpose |
| --- | --- |
| `GET /v1/tenant/api-keys` | List the selected tenant's key metadata. |
| `POST /v1/tenant/api-keys` | Create an allowed `tenant` or `management` key and return its secret once. |
| `DELETE /v1/tenant/api-keys/{id}` | Revoke a key belonging to the selected tenant. |
| `GET /v1/tenant/audit-events` | Read paginated change history for the selected tenant. |
| `GET /v1/subtenants/{id}/audit-events` | Read paginated history for a permitted direct child. |

### Existing endpoints

The configuration and child-management endpoints from Feature 001 also accept an authorized user session. Existing key-based clients retain their behavior. Child-management endpoints require either the existing management key or an authorized parent administrator session.

Authentication is unambiguous: when an `Authorization` header is supplied on a tenant API request, validate that key and do not fall back to a cookie if it fails. Without that header, use the browser session. Authentication endpoints and the new current-tenant key-management endpoints use sessions, not API keys.

Return JSON errors and HTTP status codes, not redirects to an HTML login page: `401` for missing or expired authentication, `403` for insufficient permissions, `404` for an out-of-scope resource, and `409` for changed tenant context. Invalid or missing antiforgery tokens reject the request without performing the action.

## 7. Data additions

This is a logical schema for Feature 002; the implementation should use Identity's supported user storage rather than introducing a second password store.

| Record | Important fields and rules |
| --- | --- |
| User | ID, normalized unique login email, framework-managed password hash and security state, disabled state, creation time. |
| Tenant membership | User ID, tenant ID, role (`admin`), creation time, removal time. One membership per user and tenant; reactivation updates that membership. |
| User session | ID, user ID, selected tenant ID (optional), creation time, last user-activity time, absolute expiry, idle expiry, revocation time. |
| Audit event extension | Add a user actor alongside the existing API-key and administrative actors. Browser actions record the actual user; do not pretend that a tenant API key performed them. |

Keep Feature 001 tenants and API keys intact. Extend the audit actor constraint so each event has exactly one actor: a user, an API key, or a named administrative operation. Preserve existing audit records during migration.

Record successful key creation and revocation, configuration changes, password changes or resets, and membership changes. Login attempts and session revocations belong in authentication logs and must not include passwords, cookie values, antiforgery tokens, or API key secrets.

## 8. Local SPA scope

Add simple screens for:

- Login with email and password.
- Selecting a tenant when the user has several memberships.
- Current tenant configuration.
- Key listing, creation, one-time copy, and revocation.
- Existing subtenant management when permitted.
- Reading tenant change history.
- Password change, logout, and logout everywhere.

Make the selected tenant visible on every management screen. When the session expires, clear sensitive views and return to login. When access to a selected tenant is lost, clear that tenant's data and request a fresh session summary.

## 9. Acceptance criteria

1. Feature 001 integrations work before and after adding this feature without user login or regenerated keys.
2. The first NaLekci administrator can sign in, view configuration, and generate a management key without supplying an existing API key.
3. A user assigned only to a studio sees that studio and cannot access its parent, siblings, or their key-management endpoints.
4. An administrator can issue tenant keys, but cannot issue a management key when the selected tenant lacks the required capability.
5. A multi-tenant user can switch only to tenants with valid memberships. A stale tab cannot mutate a newly selected tenant by mistake.
6. A key secret is shown once, stored only as a verifier, and never returned by list endpoints. The browser session does not depend on it.
7. Page reload preserves an active login. Idle expiry, absolute expiry, current logout, and logout everywhere are enforced by the server.
8. Replaying a cookie after logout or password reset fails. Membership removal and tenant suspension take effect without waiting for cookie expiry.
9. Signing out or changing a password does not revoke the tenant's integration keys. Explicit key revocation still takes immediate effect.
10. Cookie-authenticated changes without valid antiforgery protection fail. Login failures are generic and repeated attempts are throttled.
11. Changing a tenant's contact email neither changes login credentials nor grants a membership.
12. Browser changes are attributed to the signed-in user; key-based changes remain attributed to the key.
13. Valid sessions survive an ordinary application restart when the database and cookie-protection keys are retained.
14. All flows can be tested locally with Docker PostgreSQL and the Vite SPA, without a production frontend or email service.

## 10. Deferred

Public signup, invitation emails, self-service forgotten-password recovery, email verification, social login, MFA, team-management screens, additional roles, campaigns, and payment features remain separate work.

The next step, [Feature 003](./003-bank-configuration-and-inbound-address.md), adds bank settings and a test notification address using a local email-provider stub.

## Implementation references

ASP.NET Core supports cookie authentication and request-time validation of cookie identities. Use its built-in mechanisms when implementing the session rules above: [Microsoft: cookie authentication](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/cookie).

Use the framework's antiforgery support for the local browser client: [Microsoft: preventing cross-site request forgery](https://learn.microsoft.com/en-us/aspnet/core/security/anti-request-forgery). Session durations, membership rules, and API paths in this document are Pairrot design decisions.
