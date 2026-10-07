# Receiving Pairrot payment webhooks

This is the contract for an application that receives Pairrot's payment-status webhooks ([Feature 010](./features/010-payment-status-webhooks.md), retries in [Feature 011](./features/011-webhook-delivery-retries.md)), such as NaLekci. Pairrot stays the source of payment state; a webhook tells you that a payment request changed and what it looks like now.

## Request

Pairrot sends `POST` to your HTTPS URL with a UTF-8 JSON body of at most 64 KiB and these headers:

| Header | Value |
| --- | --- |
| `Content-Type` | `application/json; charset=utf-8` |
| `Pairrot-Webhook-Id` | The delivery ID. It is the same on every attempt and replay to your endpoint. |
| `Pairrot-Webhook-Timestamp` | Unix time in seconds of this attempt. |
| `Pairrot-Webhook-Signature` | `v1,<key-id>,<base64-hmac>`. During a key rotation there are two entries separated by one space. |

```json
{
  "id": "0199b7c2-…",
  "type": "payment_request.updated",
  "schema_version": 1,
  "created_at": "2026-10-07T12:00:00.123456Z",
  "environment": "live",
  "tenant_id": "<payment owner>",
  "data": {
    "payment_request_id": "<request id>",
    "external_id": "booking_123",
    "bank_connection_id": "<connection id>",
    "sequence": 3,
    "cause": "allocation_created",
    "previous": { "status": "partially_paid", "paid_amount": 20000, "remaining_amount": 25000, "overpaid_amount": 0 },
    "current": { "status": "paid", "paid_amount": 45000, "remaining_amount": 0, "overpaid_amount": 0 },
    "expected_amount": 45000,
    "currency": "CZK"
  }
}
```

- `status` is `open`, `partially_paid`, `paid`, `overpaid`, or `cancelled`. A cancelled request has `remaining_amount: null`.
- `cause` is `allocation_created`, `allocation_revoked`, `transaction_review_required`, or `request_cancelled`.
- Amounts are integers in minor units (haléře). Parse them as signed 64-bit integers, never as floating point.
- `sequence` increases per payment request. `GET .../payment-requests/{id}` returns the latest one as `payment_change_sequence`.
- `tenant_id` is the payment owner. `external_id` is unique only within that tenant.
- `type: "webhook.test"` is a synthetic probe with `data.synthetic: true`. Acknowledge it and change nothing.

## Verifying the signature

1. Read the raw body bytes before parsing JSON.
2. Reject a timestamp that differs from your clock by more than five minutes.
3. Compute HMAC-SHA256 over the ASCII delivery ID, `.`, the decimal timestamp, `.`, and the raw body bytes, with nothing added.
4. The key is the secret shown once by Pairrot, `pwhsec_<base64url>`. Decode the part after `pwhsec_` (base64url without padding) to 32 bytes.
5. Encode the HMAC as standard Base64 and compare it in constant time with the entry whose key ID you accept. Accept the request if any entry matches.

Test vector (also in `tests/Pairrot.UnitTests/WebhookRulesTests.cs`):

| Input | Value |
| --- | --- |
| Secret | `pwhsec_AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8` (bytes 0x00…0x1f) |
| Delivery ID | `01900000-0000-7000-8000-000000000001` |
| Timestamp | `1791374400` |
| Body | `{"id":"01900000-0000-7000-8000-0000000000aa","type":"payment_request.updated"}` |
| Signature | `IO/pJAG2O51hyi8WurN0lY2gbAnoj9z/aI2jU9YuTAQ=` |

## Handling the event

1. Verify the signature and timestamp. Then validate `schema_version`, `type`, `environment`, and that you know `tenant_id`.
2. Find the reservation by the payment owner and `external_id`, and keep `payment_request_id`.
3. In one local database transaction, store a receipt for the event `id` and apply the payment summary.
4. If you already have a receipt for the `id`, or the `sequence` is not higher than the one you applied for that payment request, answer `2xx` and do nothing else.
5. For a higher `sequence`, apply the whole `current` snapshot. It may reduce the paid amount or move a paid reservation back to partially paid or open; deciding what that means for the booking is up to you.
6. Answer `2xx` only after the change is durable. Put slow follow-up work in your own queue. You may durably queue a verified event whose reservation you do not know yet, but never answer `2xx` after silently dropping it.

A failed signature or schema check must not change anything; answer `401` or `400`.

## Delivery and retries

- Any `2xx` is success. Pairrot does not follow redirects, and the request times out after 10 seconds.
- Network errors, timeouts, `408`, `425`, `429`, and `5xx` are retried 5 seconds, 60 seconds, and 1 hour after each failure, four attempts in total. `Retry-After` can postpone a retry, but never adds one.
- Other `4xx` codes and redirects are final. After a final failure, an administrator can retry or replay the delivery in Pairrot.
- Every attempt carries the same delivery ID and body, with a new timestamp and signature. Exactly-once delivery is not promised: a request you accepted just before a timeout can arrive again. Steps 3 and 4 make that harmless.
- Order is not guaranteed. A correction can arrive before an older event, and the sequence check keeps the newer state.
- Gaps in `sequence` are normal, e.g. when you subscribed later. For current state, the newest snapshot is enough. For the full history, read `GET .../payment-requests/{id}/changes`. To reconcile, read the request and apply it with the same sequence check.

## .NET example

```csharp
app.MapPost("/pairrot/webhook", async (HttpRequest request, ReservationDb db) =>
{
    using var buffer = new MemoryStream();
    await request.Body.CopyToAsync(buffer);
    var body = buffer.ToArray();

    // Pairrot.Core.Webhooks.WebhookSignature, or copy its 30 lines.
    var keys = new Dictionary<string, byte[]> { [settings.KeyId] = WebhookSignature.ParseSecret(settings.Secret)! };
    if (!WebhookSignature.Verify(keys, request.Headers["Pairrot-Webhook-Id"], request.Headers["Pairrot-Webhook-Timestamp"],
            request.Headers["Pairrot-Webhook-Signature"], body, DateTimeOffset.UtcNow))
    {
        return Results.Unauthorized();
    }

    using var json = JsonDocument.Parse(body);
    var root = json.RootElement;
    if (root.GetProperty("type").GetString() != "payment_request.updated")
    {
        return Results.Ok(); // webhook.test and future types: acknowledge, no effect
    }

    var eventId = root.GetProperty("id").GetGuid();
    var data = root.GetProperty("data");
    var requestId = data.GetProperty("payment_request_id").GetGuid();
    var sequence = data.GetProperty("sequence").GetInt32();
    var current = data.GetProperty("current");

    await using var tx = await db.Database.BeginTransactionAsync();
    if (await db.WebhookReceipts.AnyAsync(r => r.EventId == eventId))
    {
        return Results.Ok();
    }

    db.WebhookReceipts.Add(new WebhookReceipt(eventId));
    var reservation = await db.Reservations.SingleAsync(r =>
        r.PairrotTenantId == root.GetProperty("tenant_id").GetGuid() && r.ExternalId == data.GetProperty("external_id").GetString());
    if (sequence > reservation.PaymentSequence)
    {
        reservation.PaymentSequence = sequence;
        reservation.PaymentStatus = current.GetProperty("status").GetString()!;
        reservation.PaidAmount = current.GetProperty("paid_amount").GetInt64();
    }

    await db.SaveChangesAsync();
    await tx.CommitAsync();
    return Results.Ok();
});
```

`tests/Pairrot.IntegrationTests/WebhookReceiverTests.cs` runs the same steps against a local receiver: an outage, a manual retry, a correction, and a replayed older event.
