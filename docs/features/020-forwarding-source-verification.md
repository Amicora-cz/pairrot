# Feature 020: forwarding source verification and provider samples

Status: proposed. Extends [Feature 008](./008-forwarded-bank-notifications.md).

## Goal and scope

Feature 008 lets a tenant register a forwarding mailbox by typing its address, and the mailbox is active at once. Pairrot does not check that the tenant controls it. The accepted risk was that a mistyped or foreign address makes mail authenticated for that mailbox valid for the connection. In practice the common failure is the other way round: a typo means the tenant's real forwards fail the source check and every payment lands in review, and nothing tells them why beyond `bank_message_untrusted`.

Feature 008 also left the second mail provider unverified: automatic forwards from Seznam have no recorded sample, so Pairrot cannot say which evidence authenticates them, and over Resend the only evidence for an automatic forward is the bank's own DKIM surviving.

This feature verifies control of the mailbox before its forwards count, keeps the legacy behavior for sources registered before it, and records the Seznam samples that Feature 008 asked for.

## 1. Source states

| State | Meaning |
| --- | --- |
| `pending_verification` | Registered, not yet proven. Attributable forwards are retrieved and do not count as foreign mail. Unless the message itself supplies successful inbound proof below, source-dependent trust is `untrusted` with `forwarding_source_unverified` and requires review. |
| `active` | Allowed for forwarding under Feature 008, either explicitly verified or grandfathered as legacy. Check `verified` to distinguish these. |
| `removed` | As today. |

A source records `verified_at` and `verified_by`: `code`, `inbound_message`, `legacy`, or `operator`. Legacy sources have `verified_at = null` and `verified = false`; successful explicit verification sets the time/method and `verified = true`.

`Forwarding:RequireVerification` (default `true`) makes new sources start as `pending_verification`. With `false` they start `active` with `verified_by = legacy`, which is today's behavior. Sources existing at migration become `active`, `verified_by = legacy`, and the API shows `verified: false` for them so a parent can ask the tenant to verify.

Both verification methods accept `pending_verification` and `active` legacy-unverified sources. Verification never temporarily downgrades a legacy source: sending/expiring a code or a failed proof leaves its active forwarding behavior intact. Removed sources cannot be verified; verification checks the current source identity/state and tenant erasure barrier at commit.

## 2. Two ways to verify

**Inbound proof (no sending needed).** The tenant sends e-mail from the mailbox to the connection's current Pairrot address. A live received delivery whose exact normalized From matches a pending or active legacy-unverified source, with DMARC `pass` for that domain from the trusted receiving system, verifies it. Fixtures, test deliveries, quoted authentication headers and mail to retired/paused addresses cannot activate a live source. Ordinary proof mail is classified `verification`, uses confirmation retention of 7 days, is never parsed as a payment, and appears as `verification_received` without counting as foreign mail.

A forwarded bank notification may itself supply the proof: commit verification before assessing source-dependent trust for its parse, then process it as a manual forward under Feature 008, with ordinary bank-message retention. This is an explicit exception to the ordinary proof-mail classification. Source verification alone does not make a template parseable, prove a bank payment, or establish reception readiness. A duplicate delivery cannot create another verification audit event; a source removed while proof was being retrieved cannot be reactivated by a late result.

**Code by e-mail.** With an outbound sending configuration (`Outbound:Provider = resend`, `Outbound:ApiKey`, `Outbound:FromAddress` on a verified sending domain), `POST .../forwarding-sources/{id}/send-code` sends a six-digit code to the mailbox, valid for 15 minutes, at most 3 sends per hour per source. `POST .../forwarding-sources/{id}/verify { code }` activates the source; five wrong codes invalidate the code. The e-mail names the connection's label and the Pairrot address and contains no payment data. Without outbound configuration the endpoint is `409 outbound_not_configured` and only inbound proof is available.

Codes are cryptographically random; store only a keyed digest and compare without timing-dependent equality. A new code invalidates the previous challenge. Send limits also apply per tenant and normalized mailbox so removing/recreating a source does not reset them. Attempt increments, expiry checks and single-use consumption are atomic. Idempotent replay of a successful verify with the same command key returns the stored `200`; reuse under a different key is rejected. Replaying `send-code` never sends another message or resets attempt/expiry limits. Feature 014 purges challenge secrets and fences late outbound jobs for erased tenants.

Both ways are audited (`forwarding_source.verified` with the method). An operator can verify by CLI (`forwarding verify <source-id>`), recorded as `operator`.

## 3. Trust and readiness

- Feature 008's source-dependent validity table applies only to `active` sources. A forward attributed to a pending source that does not itself pass inbound proof is untrusted with `forwarding_source_unverified`; its local review case names the source. Bank-authenticated mail remains eligible through Feature 008's bank-origin path independently of a forwarding-source claim; inability to attribute an automatic forward is not evidence of source ownership.
- Readiness follows Feature 015 (deferred; this rule applies once it is built): pending sources block only when there is no successful trusted parsed incoming payment through a currently usable path. A pending additional source is a warning on an otherwise ready connection. Ordinary verification mail cannot satisfy payment-processing proof.
- Resolving such a review case by `confirm_payment` remains a business decision and does not verify the source.

## 4. Seznam and the forwarding-evidence table

Acceptance criterion 1 of Feature 008 is still open for Seznam. This feature closes it by recording, as sanitized fixtures with their authentication evidence:

- a Seznam automatic forward of a real Česká spořitelna notification (filter → forward), noting whether the bank's DKIM survives and what Seznam adds;
- a Seznam manual forward, with the quoted-original format;
- Seznam's forwarding-confirmation message, if it sends one, so its sender joins the configured confirmation senders.

The profile and `GET /v1/banks` then list Seznam under `verified_forwarders` with the evidence option that works, or state explicitly that automatic forwards from Seznam are unverifiable over Resend and that manual forwards or direct delivery are the supported paths for Seznam users. Nothing is listed as verified without the sample.

The same table records, per provider, whether the forwarded message's envelope sender is available from the receiving system; over Resend it is not, so the SPF option of Feature 008 stays disabled there, and the documentation says so where the tenant reads the setup guide.

## 5. API

| Route (under `/v1/tenant` and `/v1/subtenants/{subtenantId}`) | Behavior |
| --- | --- |
| `POST .../bank-connections/{id}/forwarding-sources` | As today; the response shows `state`, `verified`, `verified_by`. |
| `POST .../forwarding-sources/{sourceId}/send-code` | Pending or active legacy-unverified source: send a code, `202`, rate limited. `409 outbound_not_configured`, `409 forwarding_source_already_verified`, or `409 forwarding_source_removed`. |
| `POST .../forwarding-sources/{sourceId}/verify` | `{ code }`; `200` active, `400 invalid_code`, `409 code_expired`, `429` after too many attempts. |
| `GET .../bank-connections/{id}/reception` | Shows pending sources, `verification_received`, and the last untrusted forward's source. |

Browser writes keep antiforgery and the expected-tenant header. All POSTs take `Idempotency-Key`.

## 6. Acceptance criteria

1. A new source is pending; a source-dependent forward lacking successful inbound proof is retrieved, untrusted with `forwarding_source_unverified`, reviewable and not foreign. A message providing successful proof follows criterion 2 instead.
2. An e-mail from the pending mailbox with DMARC `pass` for its domain verifies it; the same e-mail with DMARC `fail` or from another mailbox does not. A forwarded bank notification used as proof is also parsed and trusted.
3. The code flow sends one e-mail through a fake outbound provider, accepts the right code once, rejects wrong, expired, and reused codes, and enforces the send and attempt limits.
4. After verification, forwards satisfying Feature 008's authentication rules are trusted. Verification-only mail leaves readiness waiting for a parsed incoming payment; such a payment establishes readiness. An extra unused pending source does not block an already proven path.
5. Migrated legacy-active sources keep working with `verified: false`; both code and inbound proof can upgrade them without interruption. Wrong/expired codes do not deactivate them. `RequireVerification = false` creates sources with the same legacy behavior.
6. Seznam automatic and manual forwards of a real notification are recorded as fixtures with their evidence, and the catalog states for Seznam what is verified and what is not. Until the samples exist, the catalog says `unverified` and the tests for that provider are marked pending, not passing.
7. No endpoint reveals whether a mailbox address exists elsewhere in the system.
8. Concurrent verify calls consume a challenge once; replay of the same successful command is idempotent. Removed/recreated sources do not evade send limits, and removal/erasure racing proof cannot reactivate a source. Live sources cannot be verified by fixtures or test mail.

## 7. Not included

Reading the tenant's mailbox, verifying ownership of the bank account, and provider-specific OAuth.
