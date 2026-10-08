# Feature 015: supported banks and reception readiness

Status: proposed.

## Goal and scope

Pairrot has one bank profile (Česká spořitelna, Feature 007) and no way for an integrator to learn that before an instructor reconfigures their internet banking. A connection for another bank receives an address, but Feature 008 may classify its mail as foreign before parsing because no bank sender profile exists. Whether payments will be recognized is spread over `address_status`, `reception`, forwarding setup and parse outcomes.

This feature publishes the bank catalog and a deterministic reception-readiness evaluator per connection. It adds no parser. Feature 016 persists the evaluator's projection to detect and publish changes; the two features share one definition of readiness.

## 1. Bank catalog

`GET /v1/banks` for any authenticated caller (API key or session) returns the installed profiles from code, plus the Czech bank codes Pairrot knows by name:

```json
{
  "items": [
    {
      "bank_code": "0800",
      "name": "Česká spořitelna",
      "support": "supported",
      "profile": "ceska_sporitelna",
      "parser_version": "1.0.0",
      "notification": {
        "product": "George",
        "path": "Nastavení → Informační zprávy → Příchozí platby",
        "sender": "ceskasporitelna@csas.cz",
        "single_recipient": true
      },
      "delivery_paths": ["direct", "automatic_forward", "manual_forward"],
      "verified_forwarders": ["gmail.com"],
      "verified_samples": ["incoming_with_vs"],
      "unverified_samples": ["incoming_without_vs", "outgoing", "correction", "text_part"]
    },
    { "bank_code": "0100", "name": "Komerční banka", "support": "unsupported" }
  ]
}
```

- `support` is `supported` (a profile exists), `planned` (an operator-configured list, `Banks:Planned`, so an integrator can say "soon"), or `unsupported`.
- Everything under a supported bank comes from the profile: `verified_forwarders` from `Forwarding:SpfVerifiedForwarderDomains` and the providers whose samples the profile records; `verified_samples` and `unverified_samples` from a new `IBankProfile.Samples` list, so the README's "not yet seen in a real sample" is machine-readable.
- `notification.path` and `sender` are facts for the integrator's own setup guide, not strings to display verbatim. Pairrot does not localize them.
- The response is cacheable (`Cache-Control: public, max-age=3600`) because it changes only with a deployment.

## 2. Bank connections

- Responses gain `bank: { bank_code, name, support }` and `parser_supported` (`true` only for `supported`).
- Creation of a connection at an unsupported bank stays allowed, so an address can be prepared ahead of a parser, but the response carries `warnings: ["bank_unsupported"]`. A caller that wants to refuse up front sends `?require_supported=true` and gets `409 bank_unsupported`.
- An account whose bank code has no entry in the catalog at all is still a valid Czech account (Feature 003 validation is unchanged); it is `unsupported`.

## 3. Readiness

`readiness` appears on the connection response and on `GET .../bank-connections/{id}/reception`:

```json
{ "state": "not_ready", "reasons": ["forwarding_unverified", "waiting_for_first_message"], "warnings": [], "checked_at": "…" }
```

`state` is `ready` only when there are no blocking reasons below. Evidence comes from live, received messages on the **current** inbound address; fixtures, test messages, foreign mail and forwarding confirmations never establish readiness. Use the latest completed relevant parse result ordered by `(received_at, message_id)`; reprocessing replaces that message's result, and late completion of an older message cannot replace a newer message's evidence. A successful result must be trusted and normalized as an incoming payment (`parsed` or `duplicate` with valid financial facts); trust alone or a recognized outgoing notification is insufficient. Matching or allocation success is not required.

Evidence whose forwarding source has been removed is no longer eligible. A retained legacy-active source remains eligible under Feature 020. Address replacement resets the evidence scope. Parent suspension counts as `tenant_not_active`. Blocking reasons appear in this order:

| Reason | Source |
| --- | --- |
| `tenant_not_active`, `bank_connection_archived` | lifecycle |
| `bank_unsupported` | section 1 |
| `no_inbound_address`, `address_not_live` (stub, pending, failed) | Feature 006 |
| `address_paused` | Feature 008 |
| `forwarding_unverified` | A pending source exists and there is no eligible successful notification through a currently usable path (direct/bank-authenticated or active forwarding source). |
| `waiting_for_first_message` | No relevant bank notification on the current address. |
| `last_bank_message_untrusted` | the latest bank notification failed origin checks |
| `last_bank_message_unsupported` | Latest relevant completed result is `unsupported`, including an authenticated unknown template. |
| `payment_processing_not_verified` | No successful result as defined above and none of the preceding message-result reasons explains it (e.g. only ignored outgoing mail, failed parsing, or content awaiting processing). |

When `waiting_for_first_message` is the only blocking reason, `not_ready` is a neutral onboarding state: NaLekci should show "Čekáme na první platbu" (waiting for the first payment), not a red error or a failure alert. This presentation applies only to that sole reason; accompanying reasons such as `address_paused`, `bank_unsupported` or `forwarding_unverified` still require the corresponding setup action. `not_ready` describes evidence/readiness, not a severity level on its own.

Independent `warnings`, in stable code order, are `unsupported_messages_received` when the 30-day unsupported count is positive and `additional_forwarding_source_unverified` when pending sources coexist with a usable successful path. Warnings do not change `state`. Thus an authenticated unknown template starts `not_ready`; a later trusted parsed payment makes the connection `ready` while retaining the historical warning until its window expires. Adding an unused pending source cannot break a proven path.

| Evidence (assuming lifecycle/address/bank checks pass) | State |
| --- | --- |
| No bank notification, or only verification/confirmation mail | `not_ready` |
| Authenticated bank mail with an unknown template | `not_ready`, `last_bank_message_unsupported` |
| Trusted incoming payment parsed, whether matched or unmatched | `ready` |
| Old unsupported message followed by a trusted parsed incoming payment | `ready` with historical warning |
| Latest relevant completed result is untrusted/unsupported | `not_ready` until superseded by a successful result |

The evaluator is pure and reusable; reads do not write events. With Feature 016 installed, reads use its persisted projection and return its actual `checked_at`. State, blocking reasons and warning changes are reported as `bank_connection.reception_changed` by the transition mechanism defined there.

`reception` also gains 30-day counters: `trusted_message_count`, `untrusted_message_count`, `unsupported_message_count`, and `latest_unsupported_at`, so an integrator can distinguish "nothing arrives" from "it arrives and we cannot read it".

## 4. Parse outcomes

Feature 007's `unsupported` reason codes distinguish `bank_unsupported` and `template_unknown` when a message reaches the parser. Existing runs are not rewritten. This does not relax Feature 008's collection rules: mail from an unknown bank sender may remain foreign and must not be fetched merely to produce a parse result. The catalog and connection report `bank_unsupported` regardless; unsupported counters count actual parse outcomes, not foreign messages.

## 5. Acceptance criteria

1. `GET /v1/banks` lists the Česká spořitelna profile with its verified and unverified samples, forwarders, and setup facts, taken from the profile and configuration, not from a second hand-maintained list; a planned bank from configuration appears as `planned`.
2. Creating a connection at an unsupported bank returns the warning; with `require_supported=true` it is refused before any address exists.
3. Readiness follows the decision table, including trusted-but-unparseable mail, a later successful payment, historical warnings, a pending unused source, source removal, address replacement, parent suspension, and late completion of an older message. Test/fixture evidence cannot make a live connection ready.
4. The counters reflect the last 30 days only and ignore foreign and confirmation mail.
5. Parser-eligible mail for a bank without a profile records `bank_unsupported`; an unknown template of a supported bank records `template_unknown`. Unknown senders remain foreign under Feature 008 and are not fetched or counted as unsupported parse results.
6. The SPA shows the bank, its support, readiness with reasons, and the counters on each connection; the catalog is reachable with a session and with an API key, and a sibling's connection is still `404`.
7. With only `waiting_for_first_message`, the SPA presents neutral waiting text; add `address_paused` or `bank_unsupported` and it presents the actionable reason instead. Integrator documentation includes the same presentation rule for NaLekci.
