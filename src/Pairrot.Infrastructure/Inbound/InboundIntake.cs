using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pairrot.Core.Inbound;
using Pairrot.Core.Jobs;
using Pairrot.Infrastructure.Jobs;
using Pairrot.Infrastructure.Persistence;
using Pairrot.Infrastructure.Tenants;

namespace Pairrot.Infrastructure.Inbound;

/// <summary>Accepts authenticated deliveries from any adapter.</summary>
public interface IInboundEmailIntake
{
    Task<IntakeResult> AcceptAsync(InboundDelivery delivery, CancellationToken ct);
}

public enum IntakeStatus
{
    /// <summary>Durably saved: a new message (routed or quarantined) or another delivery of a known email.</summary>
    Accepted,

    /// <summary>The same delivery was already saved; nothing changed.</summary>
    Duplicate,

    /// <summary>A known delivery ID arrived with different content. The original is kept.</summary>
    Conflict,

    /// <summary>The delivery breaks the contract (unknown instance, missing identities, both or no content modes).</summary>
    Rejected,

    /// <summary>Valid but not saved; the transport should retry.</summary>
    Unavailable,
}

/// <summary>A transport-independent outcome; each adapter translates it into its own acknowledgement.</summary>
/// <param name="NewMessage">This delivery recorded the email for the first time.</param>
public sealed record IntakeResult(
    IntakeStatus Status, Guid? MessageId = null, InboundRoutingState? Routing = null, string? Error = null, bool NewMessage = false);

/// <summary>
/// Common intake: validation, deduplication, routing, quarantine, and durable storage of the
/// receipt, the message, and either its inline content or its retrieval job, in one transaction.
/// Nothing here knows which vendor or transport delivered the email.
/// </summary>
public class InboundIntake(
    PairrotDbContext db,
    BackgroundJobQueue jobs,
    InboundRouter router,
    InboundProviderRegistry providers,
    ILogger<InboundIntake> logger) : IInboundEmailIntake
{
    public const int MaxIdentityLength = 200;
    private const int SaveAttempts = 3;

    public async Task<IntakeResult> AcceptAsync(InboundDelivery delivery, CancellationToken ct)
    {
        if (providers.Find(delivery.ProviderInstanceId) is not { Enabled: true } instance)
        {
            return Rejected("The provider instance is not configured or is disabled.");
        }

        if (delivery.SourceDeliveryId is not { Length: > 0 and <= MaxIdentityLength }
            || delivery.SourceMessageId is not { Length: > 0 and <= MaxIdentityLength })
        {
            return Rejected("Delivery and message identities must be 1 to 200 characters.");
        }

        if ((delivery.Content is null) == (delivery.ContentReference is null) || delivery.ContentReference is { Length: 0 or > 1000 })
        {
            return Rejected("A delivery carries either inline content or a content reference.");
        }

        if (delivery.Content is { } inline && InboundContent.Validate(inline) is { } contentError)
        {
            return Rejected(contentError);
        }

        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return await SaveAsync(instance, delivery, ct);
                }
                catch (DbUpdateException ex) when (attempt < SaveAttempts
                    && (TenantService.IsUniqueViolation(ex, InboundDeliveryReceiptConfiguration.DeliveryUniqueIndex)
                        || TenantService.IsUniqueViolation(ex, InboundMessageConfiguration.SourceMessageUniqueIndex)))
                {
                    // A concurrent delivery saved the same delivery or email first; the next attempt finds it.
                    db.ChangeTracker.Clear();
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Could not save delivery {DeliveryId} of {Instance}.", delivery.SourceDeliveryId, delivery.ProviderInstanceId);
            return new IntakeResult(IntakeStatus.Unavailable);
        }
    }

    private async Task<IntakeResult> SaveAsync(InboundProviderInstance instance, InboundDelivery delivery, CancellationToken ct)
    {
        var digest = Digest(delivery);
        var existing = await db.InboundDeliveryReceipts.AsNoTracking()
            .Where(r => r.ProviderInstanceId == instance.Id && r.SourceDeliveryId == delivery.SourceDeliveryId)
            .Select(r => new { r.PayloadSha256, r.InboundMessageId })
            .SingleOrDefaultAsync(ct);
        if (existing is not null)
        {
            return new IntakeResult(existing.PayloadSha256 == digest ? IntakeStatus.Duplicate : IntakeStatus.Conflict, existing.InboundMessageId);
        }

        var now = jobs.Now();
        var receipt = new InboundDeliveryReceipt
        {
            Id = Guid.CreateVersion7(),
            ProviderInstanceId = instance.Id,
            SourceDeliveryId = delivery.SourceDeliveryId,
            TransportKind = delivery.TransportKind,
            DeliveryEvidence = delivery.DeliveryEvidence,
            RawPayload = delivery.RawTransportPayload,
            PayloadSha256 = digest,
            AcceptedAt = now,
        };
        db.InboundDeliveryReceipts.Add(receipt);

        var recipients = EmailAddresses.NormalizeAll(delivery.EnvelopeRecipients);
        var known = await db.InboundMessages
            .SingleOrDefaultAsync(m => m.ProviderInstanceId == instance.Id && m.SourceMessageId == delivery.SourceMessageId, ct);
        if (known is not null)
        {
            // Another delivery of the same email: one message, one content, at most one retrieval.
            receipt.Outcome = InboundReceiptOutcome.ExistingMessage;
            receipt.InboundMessageId = known.Id;
            if (known.RoutingState == InboundRoutingState.MissingRecipientEvidence && recipients.Count > 0)
            {
                // The first delivery had no recipient evidence; this one does, so the normal rules decide now.
                await ResolveMissingEvidenceAsync(known, delivery, recipients, now, ct);
            }

            await db.SaveChangesAsync(ct);
            return new IntakeResult(IntakeStatus.Accepted, known.Id, known.RoutingState);
        }

        var route = await router.RouteAsync(recipients, instance.Id, ct);
        var message = new InboundMessage
        {
            Id = Guid.CreateVersion7(),
            ProviderInstanceId = instance.Id,
            SourceMessageId = delivery.SourceMessageId,
            SourceKind = instance.SourceKind,
            Environment = instance.Environment,
            RoutingState = route.State,
            TenantId = route.Address?.TenantId,
            BankConnectionId = route.Address?.BankConnectionId,
            InboundAddressId = route.Address?.Id,
            HoldReason = route.Hold,
            EnvelopeRecipients = recipients,
            RecipientEvidence = delivery.RecipientEvidence,
            EnvelopeSender = InboundContent.Limit(delivery.EnvelopeSender, 1000),
            HeaderFrom = InboundContent.Limit(delivery.HeaderFrom, 1000),
            Subject = InboundContent.Limit(delivery.Subject, 2000),
            TransportKind = delivery.TransportKind,
            DeliveryEvidence = delivery.DeliveryEvidence,
            TransportMetadata = delivery.TransportMetadata,
            ReceivedAt = delivery.ReceivedAt?.ToUniversalTime() ?? now,
            ContentMode = delivery.Content is null ? InboundContentMode.Deferred : InboundContentMode.Inline,
            ContentReference = delivery.ContentReference,
            CreatedAt = now,
            UpdatedAt = now,
        };
        if (delivery.SenderAuthentication is { } authentication)
        {
            InboundContent.ApplyAuthentication(message, authentication);
        }

        db.InboundMessages.Add(message);
        receipt.Outcome = InboundReceiptOutcome.MessageCreated;
        receipt.InboundMessageId = message.Id;

        if (delivery.Content is { } content)
        {
            // Inline content is saved with the receipt; no retrieval job exists for it.
            if (content.Size > providers.MaxContentBytes)
            {
                InboundContent.Fail(message, "content_too_large",
                    $"The content exceeds Pairrot's limit of {providers.MaxContentBytes} bytes and was not stored.", now);
            }
            else
            {
                InboundContent.Store(message, content, now);
                if (route.Address is { } address && await router.ContradictsRouteAsync(content, address, ct))
                {
                    message.HoldReason = InboundHoldReason.RecipientMismatch;
                }
            }
        }
        else if (route.State == InboundRoutingState.Routed)
        {
            // Quarantined messages wait for an operator before any content is retrieved.
            jobs.Add(BackgroundJobTypes.FetchInboundContent, message.Id, 1, BackgroundJobQueue.SystemRequester);
        }

        await db.SaveChangesAsync(ct);
        return new IntakeResult(IntakeStatus.Accepted, message.Id, route.State, NewMessage: true);
    }

    private async Task ResolveMissingEvidenceAsync(
        InboundMessage message, InboundDelivery delivery, List<string> recipients, DateTimeOffset now, CancellationToken ct)
    {
        var route = await router.RouteAsync(recipients, message.ProviderInstanceId, ct);
        message.RoutingState = route.State;
        message.EnvelopeRecipients = recipients;
        message.RecipientEvidence = delivery.RecipientEvidence;
        message.UpdatedAt = now;
        if (route.Address is not { } address)
        {
            return;
        }

        message.TenantId = address.TenantId;
        message.BankConnectionId = address.BankConnectionId;
        message.InboundAddressId = address.Id;
        message.HoldReason = route.Hold;
        if (message.ContentState == InboundContentState.Available && message.ContentRecipients is { } contentRecipients
            && await router.ContradictsRouteAsync(
                new InboundContentSnapshot(null, null, null, contentRecipients, null, SenderAuthentication.Unknown), address, ct))
        {
            message.HoldReason = InboundHoldReason.RecipientMismatch;
        }

        var existingJobs = await db.BackgroundJobs.AsNoTracking()
            .Where(j => j.Type == BackgroundJobTypes.FetchInboundContent && j.TargetId == message.Id)
            .ToListAsync(ct);
        if (message.ContentMode == InboundContentMode.Deferred && message.ContentState == InboundContentState.Pending
            && !existingJobs.Any(BackgroundJobQueue.IsActive))
        {
            jobs.Add(BackgroundJobTypes.FetchInboundContent, message.Id,
                existingJobs.Select(j => j.Generation).DefaultIfEmpty(0).Max() + 1, BackgroundJobQueue.SystemRequester);
        }
    }

    private static IntakeResult Rejected(string error) => new(IntakeStatus.Rejected, Error: error);

    /// <summary>The raw transport payload when there is one, otherwise the delivery itself.</summary>
    private static string Digest(InboundDelivery delivery) => Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes(delivery.RawTransportPayload ?? JsonSerializer.Serialize(delivery))));
}

/// <summary>Applies the common snapshot to a message; shared by inline intake and deferred retrieval.</summary>
public static class InboundContent
{
    private static readonly JsonSerializerOptions Canonical = new() { WriteIndented = false };

    public static void Store(InboundMessage message, InboundContentSnapshot snapshot, DateTimeOffset now)
    {
        message.ContentState = InboundContentState.Available;
        message.ContentStoredAt = now;
        message.TextBody = Clean(snapshot.Text);
        message.HtmlBody = Clean(snapshot.Html);
        message.Headers = snapshot.HeadersJson;
        message.ContentRecipients = snapshot.DeliveredRecipients is { } recipients ? EmailAddresses.NormalizeAll(recipients) : null;
        message.Attachments = snapshot.AttachmentsJson;
        message.RawProviderContent = Clean(snapshot.RawProviderContent);
        message.ContentSha256 = Sha256(snapshot);
        ApplyAuthentication(message, snapshot.Authentication);
        message.FetchErrorCode = null;
        message.FetchErrorMessage = null;
        message.FetchErrorAt = null;
        message.UpdatedAt = now;
    }

    /// <summary>Evidence normalized again here, so no adapter can store a value outside the common set.</summary>
    public static void ApplyAuthentication(InboundMessage message, SenderAuthentication authentication)
    {
        message.SpfResult = AuthenticationResults.Normalize(authentication.Spf);
        message.DkimResult = AuthenticationResults.Normalize(authentication.Dkim);
        message.DmarcResult = AuthenticationResults.Normalize(authentication.Dmarc);
        message.AuthenticationOrigin = authentication.Origin;
        message.AuthenticationRaw = authentication.RawJson;
    }

    public static void RecordError(InboundMessage message, string code, string error, DateTimeOffset now)
    {
        message.FetchErrorCode = code;
        message.FetchErrorMessage = BackgroundJobQueue.Truncate(error);
        message.FetchErrorAt = now;
        message.UpdatedAt = now;
    }

    /// <summary>A terminal content failure: visible until someone retries explicitly.</summary>
    public static void Fail(InboundMessage message, string code, string error, DateTimeOffset now)
    {
        RecordError(message, code, error, now);
        message.ContentState = InboundContentState.Failed;
    }

    /// <summary>Digest of the common fields only, so equivalent inline and retrieved content digest equally.</summary>
    public static string Sha256(InboundContentSnapshot snapshot) => Convert.ToHexStringLower(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            text = Clean(snapshot.Text),
            html = Clean(snapshot.Html),
            headers = snapshot.HeadersJson,
            recipients = snapshot.DeliveredRecipients is { } r ? EmailAddresses.NormalizeAll(r) : null,
            attachments = snapshot.AttachmentsJson,
            spf = AuthenticationResults.Normalize(snapshot.Authentication.Spf),
            dkim = AuthenticationResults.Normalize(snapshot.Authentication.Dkim),
            dmarc = AuthenticationResults.Normalize(snapshot.Authentication.Dmarc),
        }, Canonical)));

    public static string? Limit(string? value, int length)
    {
        value = Clean(value);
        return value is null || value.Length <= length ? value : value[..length];
    }

    /// <summary>The structural problem of a snapshot, or <c>null</c>: headers must be a JSON object and attachments a JSON array.</summary>
    public static string? Validate(InboundContentSnapshot snapshot)
    {
        if (!IsJson(snapshot.HeadersJson, JsonValueKind.Object) || !IsJson(snapshot.AttachmentsJson, JsonValueKind.Array)
            || !IsJson(snapshot.Authentication.RawJson, JsonValueKind.Object))
        {
            return "Content headers must be a JSON object, attachments and authentication results valid JSON.";
        }

        return null;
    }

    private static bool IsJson(string? value, JsonValueKind kind)
    {
        if (value is null)
        {
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.ValueKind == kind;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>PostgreSQL text cannot hold NUL, so it is replaced, also in raw provider content.</summary>
    private static string? Clean(string? value) => value?.Replace('\0', '�');
}
