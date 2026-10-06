# Feature 004: manual banking-app verification

Acceptance criterion 12 of [Feature 004](../features/004-payment-qr-generation.md). Each result applies only to the app it names. One app's result says nothing about other banks' apps. No transfer was submitted in any check.

## 2026-10-06: George (Česká spořitelna)

### ASCII message

- **Code:** `SPD*1.0*ACC:CZ6508000000192000145399*AM:450.00*CC:CZK*X-VS:0000123456*MSG:Pairrot test - neodesilat`, generated from commit `cd87e3a`. The account is the CNB example 19-2000145399/0800.
- **Result:** the app read the code and showed the payment correctly.

### Percent-encoded message

- **Code:** `...*MSG:K%C5%AF%C5%88 100%25 %2A %C5%BElu%C5%A5`, which should decode to `Kůň 100% * žluť`.
- **Result:** the app showed the `%` escapes literally and refused to send the payment because of the `%` characters.
- **Consequence:** Pairrot no longer percent-encodes `MSG`:
  - The message goes into the payload unchanged and is encoded in the QR code as UTF-8.
  - `*` and `%` are rejected with `400 invalid_message`.
  - The 60-character limit counts characters.

### UTF-8 Czech message

- **Code:** `SPD*1.0*ACC:CZ6508000000192000145399*AM:77.77*CC:CZK*X-VS:0000000777*MSG:Příliš žluťoučký kůň - neodesílat`, generated after the change above.
- **Result:** the app showed the account, the amount, the variable symbol, and the message with its diacritics as expected.
