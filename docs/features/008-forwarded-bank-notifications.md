# Feature 008: forwarded bank notifications and protection against foreign mail

## Goal and scope

Let a tenant keep their bank's notifications in their own mailbox and forward only those notifications to their Pairrot address. A notification that the tenant's own registered mailbox verifiably forwarded is valid. It is recorded like a direct notification, so tenants do not end up with a pile of reviewable copies. Pairrot must not collect the tenant's other mail, and must stop processing an address that receives foreign mail.

Česká spořitelna sends George's "Informační zprávy" to one contact e-mail only. A lecturer will usually not replace their personal contact address with a Pairrot address, because they would stop receiving the bank's messages. The practical setup is a forwarding rule in their own mailbox (Gmail, Seznam, or another provider) that forwards only payment notifications.

This feature extends [Feature 006](./006-inbound-email-reception.md) intake and [Feature 007](./007-bank-message-parsing.md) trust. It does not change how transactions are recorded, matched, or reported. Direct delivery from the bank stays supported. Every transaction records its origin path (`direct`, `automatic_forward`, `manual_forward`), so a later policy can treat the paths differently without reprocessing.

Non-goals:

- Reading the tenant's mailbox (IMAP, OAuth, Gmail API).
- Manually trusting a single message or marking a payment paid. This remains with the later matching and review feature. Registering a forwarding mailbox is not a manual trust of a message.
- Rejecting mail at the SMTP level. Resend accepts every message sent to the receiving domain and offers no inbound sender blocklist. Pairrot can only decide what to retrieve, store, and process. Each received e-mail still counts toward the Resend quota.

## 1. Delivery paths

| Path | How it arrives | What Pairrot can verify |
| --- | --- | --- |
| Direct | The bank sends to the Pairrot address. | From is the bank; the receiving system's DMARC result for the bank's domain. Feature 007 already handles this. |
| Automatic forward | A filter in the tenant's mailbox redirects matching messages unchanged. | From usually stays the bank's address. The bank's DKIM may or may not survive, and SPF for the bank fails. The forwarding mailbox can be authenticated instead (section 4). |
| Manual forward | The tenant presses "Forward". | From is the tenant's mailbox, authenticated by DMARC for its domain. The bank message is quoted inside the body. |
| Foreign mail | The forwarding rule is too broad, or someone else writes to the address. | From is neither the bank, a registered forwarding source, nor a known forwarding service. |

Forwarding behavior is not assumed. Before relying on a path, obtain real samples of Gmail and Seznam forwarding an actual Česká spořitelna notification, both automatic and manual. For each sample, record:

- The From and envelope sender.
- The Resend `authentication` results.
- Whether the bank DKIM signature validates.
- The ARC headers that were added.
- How the quoted original appears in a manual forward.

Keep sanitized fixtures as in Feature 007, and do not invent forwarding headers or provider behavior.

## 2. Forwarding sources

A tenant may register forwarding sources for a bank connection, for example `lektor@gmail.com`:

- A source is the mailbox address that forwards to the connection's Pairrot address. Each source is stored normalized and is unique per connection.
- A source has a state of `active` or `removed`. Only an `active` source makes forwarded notifications valid.
- **Entered by hand (MVP):** the tenant types the mailbox address and it is active at once. Pairrot does not verify that the tenant controls the mailbox and sends no e-mail. Registration and removal are audited. A source belongs to one connection. The same mailbox may be entered for several connections of the same tenant.
- **Accepted risk:** a mistyped or foreign address makes mail authenticated for that mailbox valid for this connection. Its owner would also have to know the connection's random Pairrot address. Verifying control of the mailbox is deferred beyond the MVP. Possible ways are a code mailed to the mailbox, or a code the tenant sends from the mailbox to the Pairrot address.
- **Not proof by itself:** a From or Return-Path that names a source proves nothing, because anyone can write those headers. Section 4 defines the authentication that must accompany it.
- **No effect on routing:** registering a source does not change routing. Routing still uses only the delivered recipient and the address map from Feature 006.

### Forwarding confirmation messages

Mail providers usually confirm a new forwarding target by sending a message with a code or link to it. Gmail does this. Seznam's behavior must be checked.

- **Allowed senders:** Pairrot keeps a configured, code-reviewed list of confirmation senders for each supported provider, e.g. Gmail's forwarding service address once a real sample confirms it.
- **Short retention:** a confirmation message from an allowed sender is retrieved and stored with a short retention, by default 7 days. It is shown to the tenant who owns the address so that they can complete the setup.
- **Code display:** the SPA shows the confirmation code as text. It never follows or opens the link automatically, and it shows no HTML.
- **No parsing:** confirmation messages are not parsed as bank messages and do not count as foreign mail.
- **Unknown senders:** a confirmation-like message from an unlisted sender is foreign mail.

## 3. Sender classification before retrieval

Classify each routed delivery from its delivery metadata, before content is retrieved or stored:

| Class | Rule | Content |
| --- | --- | --- |
| `bank` | From matches the sender policy of the connection's bank profile. | Retrieved and parsed as today. |
| `forwarding_confirmation` | From matches a configured confirmation sender. | Retrieved, shown to the tenant, kept for a short time, not parsed. |
| `tenant_forward` | From matches an active forwarding source of the connection (manual forward). | Retrieved and parsed. Section 4 decides validity. |
| `foreign` | Anything else. | Not retrieved and not stored. |

For a foreign message, keep only minimal metadata: received time, sender domain, delivery and message identities for deduplication, and the classification reason. Do not keep the subject, the full sender address, the recipients beyond the routed address, or the provider's raw payload. Delete this metadata after a configured retention, by default 30 days. Discovery from Feature 006 applies the same classification, so a recovered message is never stored in more detail than a webhook delivery would be.

Classification uses the From value only to reduce collection. It is not a trust decision. A message classified as `bank` still has to pass Feature 007's origin checks.

An inline adapter applies the same classification before storing content and discards a foreign body instead of saving it.

## 4. Validity of forwarded notifications

A forwarded notification is valid when Pairrot can verify that the tenant's registered mailbox forwarded it. The tenant then vouches for it, as they would by forwarding it to an accountant. The trust decision becomes `trusted`, with the origin path and the forwarding evidence recorded on the parse run and the transaction.

| Situation | Decision |
| --- | --- |
| Direct, DMARC `pass` for the bank | Trusted, `direct` (Feature 007). |
| Automatic forward, DMARC `pass` for the bank (the bank's DKIM survived) | Trusted, `automatic_forward`. This is the same evidence as direct delivery. |
| Automatic forward, DMARC not `pass` for the bank, forwarding mailbox authenticated (below) | Trusted, `automatic_forward`. |
| Manual forward: From is an active source with DMARC `pass` for its domain, and the body contains a recognized notification of the connection's bank | Trusted, `manual_forward`. The quoted notification is parsed with the bank profile's forwarded-message template. |
| From names an active source but DMARC for it is not `pass` | Untrusted. Somebody else may be impersonating the tenant's mailbox. |
| Forwarded by a mailbox that is not an active source of this connection | Untrusted. The message is classified as foreign mail when the From is not the bank's (section 3). |

### Authenticating the forwarding mailbox of an automatic forward

The From of an automatic forward is still the bank's, so the forwarder must be identified by other receiving-system evidence. At least one of the following must hold, and the samples from section 1 decide which ones a provider supports:

- **SPF:** the envelope sender is the active source, or that provider's documented forwarding form of it (for example a `+caf_` variant on Gmail). SPF for the envelope sender's domain is `pass` according to the receiving system. The adapter must supply the envelope sender and its SPF result. When it cannot, this option is unavailable.
- **ARC:** an ARC chain that Pairrot verifies cryptographically from the raw MIME message, sealed by the source's provider (for example `google.com` for a Gmail source). ARC or Authentication-Results header text is never accepted unverified, and Resend's normalized results do not include ARC. Retrieving raw MIME follows Feature 006's retrieval, retry, and size rules.
- **DKIM:** a DKIM signature of the source's own domain that passes, if the provider adds one when forwarding.

Each option is enabled per mail provider only after its real sample shows that it works. Until then, automatic forwards whose bank DMARC fails require review. The SPA tells the tenant which providers are verified.

### Why this is acceptable, and its limits

- **Spoofed bank mail:** Česká spořitelna publishes a DMARC policy of `p=reject` (`_dmarc.csas.cz`, checked on 2026-10-07). A mailbox provider that enforces DMARC, such as Gmail, rejects mail that falsely claims to come from `csas.cz` before any forwarding filter runs. The bank profile records the policy observed for its domain. Providers accepted as forwarders must enforce DMARC.
- **Spoofed tenant mailbox:** an outsider cannot pass DMARC or SPF for the tenant's own mailbox, so they cannot imitate a forward without control of that mailbox.
- **The tenant can edit a manual forward.** A manually forwarded notification is only as reliable as the tenant who forwards it. This is an accepted risk, because the tenant is the payee. Transactions keep their origin path, so NaLekci or a later policy can still require `direct` or `automatic_forward` for particular cases.
- **Payment checks still apply:** the parser still applies every Feature 007 check to the notification content: account agreement, amounts, dates, symbol, duplicates, debits, and corrections. Forwarding never bypasses them.
- **Duplicate copies:** if the same notification reaches the address both directly and by forwarding, or is forwarded twice, the copies are separate messages. Without a bank reference, Feature 007's lookalike rule flags them for review. The profile's comparison should therefore include facts that distinguish real payments, and the setup guide must warn against combining direct delivery with forwarding.

## 5. Foreign-mail limit and address pause

Protect each address from broad forwarding rules and spam:

- **Counting:** count `foreign` deliveries per inbound address in a sliding window, by default 5 within 24 hours. Duplicate deliveries of one message count once.
- **Pausing:** when the limit is exceeded, the address becomes `paused_foreign_mail`. This is a hold on processing, separate from Feature 006's `retired` state. The address still routes, so mail is never sent to another owner.
- **Paused address:** a paused address does not retrieve or parse any further delivery, including messages classified as `bank`. Pairrot records only minimal metadata and counts, so that the tenant can see that mail is still arriving.
- **Notification:**
  - The pause is recorded in the tenant audit history.
  - The bank settings and the inbound message list show a warning, for example "This address receives e-mails that are not bank notifications, so processing is paused. Check your forwarding rule."
  - The operator CLI lists paused addresses.
- **Resuming:** the tenant or a permitted parent can resume the address. Resuming resets the window and is audited. Messages received while paused are not retrieved retroactively, because their content may be foreign. A later bank notification is processed normally.
- **Replacing:** the tenant can replace the address. The paused one becomes retired and stays reserved and paused, so future mail to it is counted and dropped.
- **Repeated pausing:** if an address is paused again within a configured period (default 7 days) after a resume, resuming is refused for the tenant. Only an operator can resume it, or the tenant replaces the address.

### Global protection

- **Daily volume alert:** the operator CLI and logs report deliveries per provider instance per day. An operator alert is raised when a configured daily volume is exceeded, because foreign mail still consumes the provider quota.
- **Webhook behavior:** webhooks for paused or foreign mail return a success acknowledgement after recording, so the provider does not retry them.

## 6. Tenant setup guidance

For each bank connection, the SPA explains both supported setups:

1. **Automatic forwarding (default):** keep the bank messages in the tenant's mailbox and forward only payment notifications.
   - Enter the forwarding mailbox in Pairrot.
   - Create a filter that forwards to the Pairrot address. Use a narrow filter: the bank sender and the payment subject, taken from the Česká spořitelna profile.
   - Show provider-specific steps for Gmail and Seznam.
2. **Direct:** set the Pairrot address as the bank's message e-mail. In George: Nastavení → Informační zprávy, activate "Příchozí platby". This replaces the contact e-mail for the bank's messages.
3. **Manual forwarding:** forwarding a single notification from the registered mailbox is accepted, for example a payment the filter missed.

The guide must state the following:

- Only payment notifications may be forwarded. Other mail pauses the address.
- Use one path per account. Combining direct delivery with forwarding, or forwarding the same notification twice, creates copies that need review.
- Forwarding from a mail provider that Pairrot has not verified yet may require review.
- Pairrot never reads the mailbox. It sees only what is forwarded.

### Setup status

The connection shows a setup status derived from received mail, so the tenant can see that the rule works:

| Status | Meaning |
| --- | --- |
| `waiting_for_first_message` | No mail has been received at the address yet. |
| `confirmation_received` | A provider's forwarding confirmation is waiting. The code is shown. |
| `bank_message_trusted` | The latest bank notification was valid. Its path (direct, automatic forward, manual forward) is shown. |
| `bank_message_untrusted` | A bank notification arrived, but neither the bank nor a registered forwarding mailbox could be verified. Review is required, and the SPA explains the likely cause (wrong mailbox entered, unverified provider, failed authentication). |
| `paused_foreign_mail` | Section 5. |

## 7. API

| Current-tenant route | Behavior |
| --- | --- |
| `GET /v1/tenant/bank-connections/{id}/forwarding-sources` | List the connection's sources and states. |
| `POST /v1/tenant/bank-connections/{id}/forwarding-sources` | Register a mailbox address, active at once. Returns `201`, or `200` with the existing source. |
| `DELETE /v1/tenant/bank-connections/{id}/forwarding-sources/{sourceId}` | Mark the source removed. |
| `GET /v1/tenant/bank-connections/{id}/reception` | Setup status, address state, foreign-mail counts, and the latest confirmation code if one is waiting. |
| `POST /v1/tenant/bank-connections/{id}/inbound-address/resume` | Resume a paused address (section 5). Returns `409` when only an operator may resume it. |

Mirror the routes under `/v1/subtenants/{subtenantId}` with the existing direct-child permissions. Browser writes require antiforgery and the expected-tenant header. Foreign mail never appears in the inbound message list as a normal message. The tenant sees only counts and sender domains. Archived connections and inactive tenants follow Feature 006's hold rules.

Operator CLI:

- `inbound paused` lists paused addresses.
- `inbound resume <address>` resumes one.
- `inbound volume` shows daily deliveries per instance.
- `inbound show` displays the classification and forwarding evidence.

## 8. Logical schema

| Table/change | Important fields and constraints |
| --- | --- |
| `forwarding_sources` | Tenant, connection (composite owner reference), normalized address, state, timestamps. Unique per connection and address among non-removed sources. |
| `inbound_addresses` | Processing state (`active`, `paused_foreign_mail`), paused time and reason, resume count, last resume time. Separate from `state` (`active`/`retired`). |
| `inbound_messages` | Sender class, forwarding evidence summary, retention deadline. Foreign messages store no subject, full sender, body, headers, or raw payload. A check constraint enforces this. |
| `foreign_mail_counters` or a derived query | Foreign deliveries per address per window. Must deduplicate repeated deliveries of one message. |
| Parse run trust evidence | Origin path (`direct`, `automatic_forward`, `manual_forward`), the forwarding source, the evidence that authenticated it (SPF, verified ARC, DKIM, or DMARC of the source), and the bank domain's DMARC policy. |
| `incoming_transactions` | Origin path of the establishing message, so later policy can distinguish direct and forwarded receipts. |

A retention job deletes expired foreign metadata and confirmation content, and records the counts it deleted. It never deletes bank messages, parse runs, or transactions.

## 9. Acceptance criteria

1. **Forwarding samples:** real Gmail and Seznam automatic and manual forwards of a Česká spořitelna notification are recorded as sanitized fixtures with their authentication evidence. The evidence option that authenticates each provider's forwarder is documented. Without samples, that provider's forwarding is reported as unverified and requires review.
2. **Automatic forward from a registered mailbox:** it creates an accepted transaction with path `automatic_forward`, whether bank DMARC passed or the forwarding mailbox was authenticated. The evidence is visible in the parse run and on the transaction.
3. **Manual forward from a registered mailbox:** with DMARC `pass` for the mailbox's domain, it creates an accepted transaction with path `manual_forward` from the quoted notification.
4. **Forwards that are not valid:** none of the following creates an accepted transaction:
   - A forward from a mailbox that was never entered or was removed.
   - A message whose From names the source but fails DMARC for it.
   - An automatic forward whose forwarder evidence is missing.
   - Header text claiming ARC or authentication results.

   Each of these requires review with its candidate kept.
5. **Forwarding sources:** a mailbox entered by the tenant is active at once. A removed mailbox no longer makes forwards valid. Registration and removal are audited.
6. **Content checks:** forwarded notifications pass every Feature 007 content check (account, amount, date, symbol, debit, correction). Copies of the same notification arriving by two paths without a bank reference are flagged, not counted twice.
7. **Foreign mail:** its content is not retrieved and its body, subject, and full sender are not stored. The same applies to inline and discovery paths. Its metadata expires after the retention period.
8. **Pausing:** exceeding the foreign-mail limit pauses the address, audits the pause, and shows the warning in the SPA. A paused address retrieves and parses nothing.
9. **Resuming:** resuming and replacing behave as specified, including the refusal for repeated pausing. Messages received while paused are never retrieved retroactively.
10. **Confirmation messages:** a provider confirmation from a configured sender is shown to the owning tenant as text with its code. It is not parsed, and it expires after the retention period. A confirmation-like message from another sender counts as foreign mail.
11. **Classification:** sender classification never changes routing or ownership, and is never by itself a trust decision.
12. **Permissions:** forwarding sources, reception status, and resume follow tenant and parent permissions. Foreign records are `404`.
13. **Webhook acknowledgements:** foreign and paused deliveries are acknowledged so that the provider does not retry them. Duplicate deliveries count once.
14. **Setup status:** it reflects the latest relevant message for direct, forwarded, untrusted, confirmation, and paused cases.

## 10. Completion boundary

Forwarding support is complete for a mail provider only when its real samples are recorded and the evidence that authenticates its forwarder is documented. A provider whose forwards cannot be authenticated by SPF, verified ARC, DKIM, or bank DMARC stays unverified, and its forwarded notifications require review until then. Manual forwards depend only on DMARC for the tenant's own mailbox domain, so they work for any provider whose domain publishes and passes DMARC.

Matching expected payments with incoming transactions, and manual resolution of reviewable candidates, follow in a later feature.
