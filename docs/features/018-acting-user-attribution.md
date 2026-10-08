# Feature 018: acting-user attribution

Status: proposed.

## Goal and scope

An integrating application performs every operation with one management key: creating requests, allocating a receipt by hand, confirming a review case, resuming a paused address. Pairrot's audit then says `api_key` with the key's ID, and allocations and decisions record `created_by`/`actor` as that key. Which of the application's users actually decided is invisible, and an application with a thousand instructors cannot issue a thousand keys to fix that.

This feature lets an API-key caller name the person on whose behalf it acts, records that name everywhere an actor is recorded, and changes nothing about authorization.

## 1. The header

| Header | Value |
| --- | --- |
| `Pairrot-Acting-User` | An opaque identifier chosen by the caller, 1–200 characters, printable, no control characters. Trimmed. |
| `Pairrot-Acting-User-Label` | Optional display text, 1–200 characters, for the SPA and the CLI. Trimmed. |

- Accepted on requests authenticated with a tenant key or a management key. On a browser session the session's user is the actor; a session request carrying the header is `400 acting_user_not_allowed`, because mixing the two is a bug worth surfacing.
- The value is a label, never an identity: it does not change the caller's tenant, purpose, or permissions, is not validated against anything, and is never used to look anything up. Authorization is the key's alone, as in Feature 001.
- Missing header: behavior is exactly today's.
- The label without `Pairrot-Acting-User`, duplicate header values, or invalid values in either header return `400 invalid_acting_user`. A session carrying either header is rejected. Rendering treats both fields as plain text.

## 2. Where it is recorded

The `Actor` carried through the services gains `ActingUser` and `ActingUserLabel`. Every place that persists an actor persists them too:

| Record | Today | With this feature |
| --- | --- | --- |
| `tenant_audit_events` | `actor_api_key_id` | plus `acting_user`, `acting_user_label`; the response's `actor` object gains both fields |
| `payment_allocations.created_by`, `revoked_by` | `"api_key:<id>"` | unchanged string, plus structured `created_by_actor` / `revoked_by_actor` in responses |
| `payment_review_decisions.actor`, transaction `handled_by` | string | the same pattern |
| `payment_request_changes.actor`, so `GET .../changes` | string | the same pattern |
| `manual_command_receipts` | key, action, fingerprint | plus the acting user, for display when a command is replayed |
| Webhook endpoint changes, rotations, retries, replays, abandonments; forwarding-source changes; address resume; bank-connection changes; erasure requests | audit only | audit with the acting user |

The acting user is **not** part of a command's idempotency fingerprint: the same `Idempotency-Key` with the same input is the same command whoever retries it, and the stored outcome is returned with `Idempotent-Replayed: true`. The receipt keeps the acting user of the first, committed attempt.

Webhook event payloads do not carry it. A receiver that wants to know who acted reads the authorized history.

## 3. Display and retention

- The SPA shows `label (id)` next to an allocation, decision, or audit row when present; the CLI prints it in `inbound show` and in audit listings.
- Erasure of a tenant ([Feature 014](./014-subtenant-erasure-and-payer-data-retention.md)) blanks the acting user and label on that tenant's records: an integrator's user ID can be personal data.
- Raw acting-user IDs and labels are not included in stdout logs, request log scopes, metrics, tracing or error-tracking payloads, including header capture. The identifier can itself be an e-mail address. Operational correlation uses Pairrot's request ID and technical key/tenant IDs; authorized database history provides the acting-user attribution and is erased by Feature 014. This avoids an additional copy whose lifetime would escape tenant erasure.

## 4. Acceptance criteria

1. A manual allocation with the header records the acting user on the allocation, the payment change, the audit event, and the command receipt, and all four show it in the API and the SPA.
2. The header grants nothing: a tenant key with an acting user still cannot use management routes, a management key still manages only direct children, and a forged value for another tenant's user changes no outcome.
3. A session request with the header is rejected; a key request without it behaves as before.
4. Replaying a command with the same key and input under a different acting user returns the stored outcome, replayed, with the original acting user.
5. Values longer than 200 characters or containing control characters are `400`; surrounding whitespace is trimmed.
6. Erasure removes the acting user from the erased tenant's records.
7. Seed an e-mail as acting user and a name as label: both appear only in authorized history, never in captured production logs or a fake error-tracking sink. Both headers have the same length/control-character validation; label-only and multi-valued headers are rejected.
