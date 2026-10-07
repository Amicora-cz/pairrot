using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pairrot.Core.Banking;
using Pairrot.Core.Inbound;
using Pairrot.Core.Parsing;
using Pairrot.Core.Tenants;
using Pairrot.Infrastructure.Persistence;
using Pairrot.Infrastructure.Tenants;

namespace Pairrot.Infrastructure.Inbound;

/// <summary>Feature 008 settings (section <c>Forwarding</c>).</summary>
public sealed class ForwardingOptions
{
    public const string Section = "Forwarding";

    /// <summary>Senders of mail providers' forwarding confirmations; replace the list to change it.</summary>
    public string[]? ConfirmationSenders { get; set; }

    public IReadOnlySet<string> ConfirmationSenderSet =>
        (ConfirmationSenders ?? ["forwarding-noreply@google.com"]).Select(s => s.Trim().ToLowerInvariant()).ToHashSet();

    /// <summary>
    /// Mail domains whose automatic forwards are authenticated by SPF of the envelope sender. Empty
    /// until a real sample proves it for a provider (spec 008, section 4).
    /// </summary>
    public string[]? SpfVerifiedForwarderDomains { get; set; }

    public IReadOnlySet<string> SpfVerifiedForwarderDomainSet =>
        (SpfVerifiedForwarderDomains ?? []).Select(s => s.Trim().ToLowerInvariant()).ToHashSet();

    /// <summary>Foreign messages tolerated within <see cref="ForeignMailWindow"/>; one more pauses the address.</summary>
    public int ForeignMailLimit { get; set; } = 5;

    public TimeSpan ForeignMailWindow { get; set; } = TimeSpan.FromHours(24);
    public TimeSpan ForeignMailRetention { get; set; } = TimeSpan.FromDays(30);
    public TimeSpan ConfirmationRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>A pause this soon after a resume can only be resumed by an operator.</summary>
    public TimeSpan OperatorResumeWindow { get; set; } = TimeSpan.FromDays(7);

}

/// <summary>
/// Decides, from delivery metadata only and before any content is retrieved or stored, how much of
/// a routed message Pairrot may collect. Foreign mail and mail to a paused address keep only
/// minimal metadata, expire, and are never retrieved. The From value used here only limits
/// collection; it is never a trust decision (the parser decides origin).
/// </summary>
public class SenderScreening(PairrotDbContext db, BankProfileRegistry profiles, TenantService tenants, IOptions<ForwardingOptions> options)
{
    /// <summary>
    /// Classifies a routed message and applies the collection rules to it (and to its receipt).
    /// Returns whether its content may be stored or retrieved. Changes are saved by the caller.
    /// </summary>
    public async Task<bool> ApplyAsync(
        InboundMessage message, InboundAddress address, InboundDeliveryReceipt? receipt, DateTimeOffset now, CancellationToken ct)
    {
        var settings = options.Value;
        var from = EmailAddresses.Normalize(message.HeaderFrom);
        var domain = from?[(from.LastIndexOf('@') + 1)..];
        message.SenderDomain = domain;
        message.SenderClass = await ClassifyAsync(address, from, domain, ct);

        var current = await db.InboundAddresses.AsNoTracking().Where(a => a.Id == address.Id).Select(a => a.Processing).SingleAsync(ct);
        if (current == InboundAddressProcessing.PausedForeignMail)
        {
            message.HoldReason = InboundHoldReason.AddressPaused;
            Minimize(message, address, receipt, now + settings.ForeignMailRetention);
            return false;
        }

        switch (message.SenderClass)
        {
            case InboundSenderClass.Foreign:
                Minimize(message, address, receipt, now + settings.ForeignMailRetention);
                await CountForeignAsync(address, now, ct);
                return false;
            case InboundSenderClass.ForwardingConfirmation:
                message.RetainUntil = now + settings.ConfirmationRetention;
                return true;
            default:
                return true;
        }
    }

    private async Task<InboundSenderClass> ClassifyAsync(InboundAddress address, string? from, string? domain, CancellationToken ct)
    {
        if (from is null || domain is null)
        {
            return InboundSenderClass.Foreign;
        }

        var iban = await db.BankConnections.AsNoTracking().Where(b => b.Id == address.BankConnectionId).Select(b => b.Iban).SingleAsync(ct);
        if (CzechBankAccount.ParseIban(iban).Account is { } account && profiles.For(account) is { } profile && profile.Senders.Allows(from, domain))
        {
            return InboundSenderClass.Bank;
        }

        if (options.Value.ConfirmationSenderSet.Contains(from))
        {
            return InboundSenderClass.ForwardingConfirmation;
        }

        var forwarded = await db.ForwardingSources.AsNoTracking().AnyAsync(s =>
            s.BankConnectionId == address.BankConnectionId && s.Address == from && s.State == ForwardingSourceState.Active, ct);
        return forwarded ? InboundSenderClass.TenantForward : InboundSenderClass.Foreign;
    }

    /// <summary>Drops everything but what deduplication, counting, and the tenant's warning need.</summary>
    private static void Minimize(InboundMessage message, InboundAddress address, InboundDeliveryReceipt? receipt, DateTimeOffset retainUntil)
    {
        message.Subject = null;
        message.HeaderFrom = null;
        message.EnvelopeSender = null;
        message.TransportMetadata = null;
        message.EnvelopeRecipients = [address.Address];
        message.ContentState = InboundContentState.Skipped;
        message.ContentStoredAt = null;
        message.ContentSha256 = null;
        message.TextBody = null;
        message.HtmlBody = null;
        message.Headers = null;
        message.ContentRecipients = null;
        message.Attachments = null;
        message.RawProviderContent = null;
        message.RetainUntil = retainUntil;
        if (receipt is not null)
        {
            receipt.RawPayload = null;
        }
    }

    /// <summary>Pauses the address when this foreign message exceeds the limit within the window.</summary>
    private async Task CountForeignAsync(InboundAddress address, DateTimeOffset now, CancellationToken ct)
    {
        var settings = options.Value;
        var since = now - settings.ForeignMailWindow;
        var earlier = await db.InboundMessages.CountAsync(m =>
            m.InboundAddressId == address.Id && m.SenderClass == InboundSenderClass.Foreign && m.CreatedAt >= since, ct);
        if (earlier + 1 <= settings.ForeignMailLimit)
        {
            return;
        }

        var tracked = await db.InboundAddresses.SingleAsync(a => a.Id == address.Id, ct);
        if (tracked.Processing != InboundAddressProcessing.Active)
        {
            return;
        }

        tracked.Processing = InboundAddressProcessing.PausedForeignMail;
        tracked.PausedAt = now;
        tracked.OperatorResumeRequired = tracked.LastResumedAt is { } resumed && now - resumed < settings.OperatorResumeWindow;
        tenants.AddAudit(Actor.Administrator("system:foreign-mail-limit"), tracked.TenantId, AuditActions.InboundAddressPaused, new
        {
            ConnectionId = tracked.BankConnectionId,
            InboundAddress = tracked.Address,
            ForeignMessages = earlier + 1,
            Window = settings.ForeignMailWindow.ToString(),
            tracked.OperatorResumeRequired,
        });
    }
}

/// <summary>Deletes expired foreign-mail metadata and forwarding confirmations (spec 008, section 8).</summary>
public class InboundRetention(PairrotDbContext db, TimeProvider time)
{
    /// <summary>Deletes expired messages that support nothing; returns how many were deleted.</summary>
    public async Task<int> PurgeExpiredAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var expired = await db.InboundMessages.AsNoTracking()
            .Where(m => m.RetainUntil != null && m.RetainUntil < now && m.LatestParseRunId == null
                && !db.TransactionSources.Any(s => s.InboundMessageId == m.Id))
            .Select(m => m.Id)
            .Take(1000)
            .ToListAsync(ct);
        if (expired.Count == 0)
        {
            return 0;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.BackgroundJobs.Where(j => expired.Contains(j.TargetId)).ExecuteDeleteAsync(ct);
        await db.InboundDeliveryReceipts.Where(r => expired.Contains(r.InboundMessageId)).ExecuteDeleteAsync(ct);
        var deleted = await db.InboundMessages.Where(m => expired.Contains(m.Id)).ExecuteDeleteAsync(ct);
        await transaction.CommitAsync(ct);
        return deleted;
    }
}

/// <summary>Runs <see cref="InboundRetention"/> at startup and then hourly in the API process.</summary>
public sealed class InboundRetentionScheduler(
    IServiceScopeFactory scopes, ILogger<InboundRetentionScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var deleted = await scope.ServiceProvider.GetRequiredService<InboundRetention>().PurgeExpiredAsync(stoppingToken);
                if (deleted > 0)
                {
                    logger.LogInformation("Deleted {Count} expired foreign-mail or confirmation messages.", deleted);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Inbound retention failed.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
