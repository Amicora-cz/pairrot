using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pairrot.Core.Banking;
using Pairrot.Core.Inbound;
using Pairrot.Core.Parsing;
using Pairrot.Core.Tenants;
using Pairrot.Core.Webhooks;
using Pairrot.Infrastructure.Persistence;
using Pairrot.Infrastructure.Tenants;

namespace Pairrot.Infrastructure.Inbound;

/// <param name="Created">A new source was registered (as opposed to returning an existing one).</param>
public sealed record RegisteredSource(ForwardingSource Source, bool Created);

public sealed record ConfirmationView(Guid MessageId, DateTimeOffset ReceivedAt, string? SenderDomain, string? Code, string Excerpt);

public sealed record LatestBankMessage(
    Guid MessageId, DateTimeOffset ReceivedAt, string? Outcome, string? TrustDecision, string? OriginPath, string? ForwardingSource);

/// <summary>What the tenant needs to see whether forwarding works (spec 008, section 6).</summary>
public sealed record ReceptionStatus(
    string SetupStatus,
    InboundAddress? Address,
    int ForeignMessagesInWindow,
    IReadOnlyList<string> RecentForeignDomains,
    ConfirmationView? Confirmation,
    LatestBankMessage? LatestBankMessage,
    IReadOnlyList<ForwardingSource> Sources);

public sealed record PausedAddressView(InboundAddress Address, int ForeignMessagesInWindow);

public sealed record DailyVolume(string ProviderInstanceId, DateOnly Day, int Messages, int Foreign);

/// <summary>
/// Feature 008: forwarding mailboxes of a connection, their confirmation by a mailed code, the
/// reception status, and resuming paused addresses. Owners are resolved like bank connections:
/// the caller's tenant, or a direct child for parent management.
/// </summary>
public partial class ForwardingService(
    PairrotDbContext db,
    TenantService tenants,
    BankProfileRegistry profiles,
    TimeProvider time,
    IOptions<ForwardingOptions> options)
{
    public async Task<ServiceResult<IReadOnlyList<ForwardingSource>>> ListAsync(
        Guid callerTenantId, Guid? subtenantId, Guid connectionId, CancellationToken ct = default)
    {
        var (_, connection) = await FindAsync(callerTenantId, subtenantId, connectionId, ct);
        if (connection is null)
        {
            return ServiceError.NotFound();
        }

        return await SourcesOf(connection.Id).ToListAsync(ct);
    }

    /// <summary>
    /// Registers a mailbox, active at once (MVP: Pairrot does not verify that the tenant controls
    /// it), or returns the source already registered.
    /// </summary>
    public async Task<ServiceResult<RegisteredSource>> RegisterAsync(
        Guid callerTenantId, Guid? subtenantId, Guid connectionId, string? address, Actor actor, CancellationToken ct = default)
    {
        var (owner, connection) = await FindAsync(callerTenantId, subtenantId, connectionId, ct);
        if (connection is null)
        {
            return ServiceError.NotFound();
        }

        if (Changeable(owner!, connection) is { } blocked)
        {
            return blocked;
        }

        if (EmailAddresses.Normalize(address) is not { } normalized)
        {
            return ServiceError.Validation("invalid_forwarding_address", "address", "address must be one e-mail address, e.g. lektor@gmail.com.");
        }

        if (await IsReservedAsync(normalized, connection, ct) is { } reserved)
        {
            return reserved;
        }

        var existing = await db.ForwardingSources.AsNoTracking().SingleOrDefaultAsync(s =>
            s.BankConnectionId == connection.Id && s.Address == normalized && s.State == ForwardingSourceState.Active, ct);
        if (existing is not null)
        {
            return new RegisteredSource(existing, false);
        }

        var now = Now();
        var source = new ForwardingSource
        {
            Id = Guid.CreateVersion7(),
            TenantId = owner!.Id,
            BankConnectionId = connection.Id,
            Address = normalized,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.ForwardingSources.Add(source);
        tenants.AddAudit(actor, owner.Id, AuditActions.ForwardingSourceRegistered, new { ConnectionId = connection.Id, Address = normalized });
        try
        {
            await db.SaveChangesAsync(ct);
            return new RegisteredSource(source, true);
        }
        catch (DbUpdateException ex) when (TenantService.IsUniqueViolation(ex, ForwardingSourceConfiguration.AddressUniqueIndex))
        {
            // A concurrent request registered the same mailbox.
            db.ChangeTracker.Clear();
            return new RegisteredSource(await db.ForwardingSources.AsNoTracking().SingleAsync(s =>
                s.BankConnectionId == connection.Id && s.Address == normalized && s.State == ForwardingSourceState.Active, ct), false);
        }
    }

    public async Task<ServiceResult<bool>> RemoveAsync(
        Guid callerTenantId, Guid? subtenantId, Guid connectionId, Guid sourceId, Actor actor, CancellationToken ct = default)
    {
        var (owner, connection) = await FindAsync(callerTenantId, subtenantId, connectionId, ct);
        var source = connection is null ? null : await db.ForwardingSources.SingleOrDefaultAsync(s =>
            s.Id == sourceId && s.BankConnectionId == connection.Id && s.State != ForwardingSourceState.Removed, ct);
        if (source is null)
        {
            return ServiceError.NotFound();
        }

        if (CheckOwner(owner!) is { } blocked)
        {
            return blocked;
        }

        var now = Now();
        source.State = ForwardingSourceState.Removed;
        source.RemovedAt = now;
        source.UpdatedAt = now;
        tenants.AddAudit(actor, owner!.Id, AuditActions.ForwardingSourceRemoved, new { ConnectionId = connection!.Id, source.Address });
        return await SaveAsync(ct) ?? (ServiceResult<bool>)true;
    }

    public async Task<ServiceResult<ReceptionStatus>> GetReceptionAsync(
        Guid callerTenantId, Guid? subtenantId, Guid connectionId, CancellationToken ct = default)
    {
        var (_, connection) = await FindAsync(callerTenantId, subtenantId, connectionId, ct);
        if (connection is null)
        {
            return ServiceError.NotFound();
        }

        return await ReceptionOfAsync(connection.Id, ct);
    }

    /// <summary>The tenant's resume; refused when the address was paused again soon after a resume.</summary>
    public async Task<ServiceResult<ReceptionStatus>> ResumeAsync(
        Guid callerTenantId, Guid? subtenantId, Guid connectionId, Actor actor, CancellationToken ct = default)
    {
        var (owner, connection) = await FindAsync(callerTenantId, subtenantId, connectionId, ct);
        if (connection is null)
        {
            return ServiceError.NotFound();
        }

        if (Changeable(owner!, connection) is { } blocked)
        {
            return blocked;
        }

        var address = await db.InboundAddresses.AsNoTracking()
            .SingleOrDefaultAsync(a => a.BankConnectionId == connection.Id && a.State == InboundAddressState.Active, ct);
        if (address is not { Processing: InboundAddressProcessing.PausedForeignMail })
        {
            return ServiceError.Conflict("address_not_paused", "The address is not paused.");
        }

        if (address.OperatorResumeRequired)
        {
            return ServiceError.Conflict("operator_resume_required",
                "The address was paused again soon after it was resumed. Fix the forwarding rule and replace the address, or ask support to resume it.");
        }

        if (await ResumeCoreAsync(address, actor, WebhookEvents.Causes.ResumedByTenant, ct) is { } error)
        {
            return error;
        }

        return await ReceptionOfAsync(connection.Id, ct);
    }

    /// <summary>Operator resume (CLI): always allowed, also for retired addresses.</summary>
    public async Task<ServiceResult<InboundAddress>> ResumeAsOperatorAsync(string address, string operatorName, CancellationToken ct = default)
    {
        var normalized = EmailAddresses.Normalize(address);
        var found = await db.InboundAddresses.AsNoTracking().SingleOrDefaultAsync(a => a.Address == normalized, ct);
        if (found is null)
        {
            return ServiceError.NotFound();
        }

        if (found.Processing != InboundAddressProcessing.PausedForeignMail)
        {
            return ServiceError.Conflict("address_not_paused", "The address is not paused.");
        }

        return await ResumeCoreAsync(found, Actor.Administrator(operatorName), WebhookEvents.Causes.ResumedByOperator, ct) is { } error
            ? error
            : await db.InboundAddresses.AsNoTracking().SingleAsync(a => a.Id == found.Id, ct);
    }

    public async Task<IReadOnlyList<PausedAddressView>> ListPausedAsync(CancellationToken ct = default)
    {
        var since = Now() - options.Value.ForeignMailWindow;
        var paused = await db.InboundAddresses.AsNoTracking()
            .Where(a => a.Processing == InboundAddressProcessing.PausedForeignMail)
            .OrderBy(a => a.PausedAt)
            .ToListAsync(ct);
        var views = new List<PausedAddressView>();
        foreach (var address in paused)
        {
            views.Add(new PausedAddressView(address, await db.InboundMessages.CountAsync(m =>
                m.InboundAddressId == address.Id && m.SenderClass == InboundSenderClass.Foreign && m.CreatedAt >= since, ct)));
        }

        return views;
    }

    /// <summary>Messages per provider instance and day, to watch the provider's quota.</summary>
    public async Task<IReadOnlyList<DailyVolume>> DailyVolumeAsync(int days, CancellationToken ct = default)
    {
        var since = Now().AddDays(-days);
        var rows = await db.InboundMessages.AsNoTracking()
            .Where(m => m.CreatedAt >= since)
            .Select(m => new { m.ProviderInstanceId, m.CreatedAt, Foreign = m.SenderClass == InboundSenderClass.Foreign })
            .ToListAsync(ct);
        return rows
            .GroupBy(r => (r.ProviderInstanceId, Day: DateOnly.FromDateTime(r.CreatedAt.UtcDateTime)))
            .Select(g => new DailyVolume(g.Key.ProviderInstanceId, g.Key.Day, g.Count(), g.Count(r => r.Foreign)))
            .OrderByDescending(v => v.Day).ThenBy(v => v.ProviderInstanceId)
            .ToList();
    }

    /// <summary>Resumes under the row lock, so the change and its <c>inbound_address.resumed</c> event (Feature 016) are saved together.</summary>
    private async Task<ServiceError?> ResumeCoreAsync(InboundAddress address, Actor actor, string cause, CancellationToken ct)
    {
        var now = Now();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var tracked = (await db.InboundAddresses.FromSql($"SELECT * FROM inbound_addresses WHERE id = {address.Id} FOR UPDATE").ToListAsync(ct)).Single();
        if (tracked.Processing != InboundAddressProcessing.PausedForeignMail)
        {
            return ServiceError.Conflict("address_not_paused", "The address is not paused.");
        }

        tracked.Processing = InboundAddressProcessing.Active;
        tracked.OperatorResumeRequired = false;
        tracked.LastResumedAt = now;
        tracked.ResumeCount++;
        db.EventCauses[tracked.Id] = cause;
        tenants.AddAudit(actor, address.TenantId, AuditActions.InboundAddressResumed, new
        {
            ConnectionId = address.BankConnectionId,
            InboundAddress = address.Address,
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return null;
    }

    private async Task<ReceptionStatus> ReceptionOfAsync(Guid connectionId, CancellationToken ct)
    {
        var now = Now();
        var address = await db.InboundAddresses.AsNoTracking()
            .SingleOrDefaultAsync(a => a.BankConnectionId == connectionId && a.State == InboundAddressState.Active, ct);
        var sources = await SourcesOf(connectionId).ToListAsync(ct);
        var since = now - options.Value.ForeignMailWindow;
        var foreign = await db.InboundMessages.AsNoTracking()
            .Where(m => m.BankConnectionId == connectionId && m.SenderClass == InboundSenderClass.Foreign && m.CreatedAt >= since)
            .Select(m => m.SenderDomain)
            .ToListAsync(ct);

        var confirmation = await db.InboundMessages.AsNoTracking()
            .Where(m => m.BankConnectionId == connectionId && m.SenderClass == InboundSenderClass.ForwardingConfirmation
                && m.ContentState == InboundContentState.Available && m.RetainUntil > now)
            .OrderByDescending(m => m.ReceivedAt)
            .FirstOrDefaultAsync(ct);

        var bank = await db.InboundMessages.AsNoTracking()
            .Where(m => m.BankConnectionId == connectionId && m.LatestParseRunId != null
                && (m.SenderClass == null || m.SenderClass == InboundSenderClass.Bank || m.SenderClass == InboundSenderClass.TenantForward))
            .OrderByDescending(m => m.ReceivedAt)
            .Select(m => new { m.Id, m.ReceivedAt, m.LatestParseOutcome, m.LatestParseRunId })
            .FirstOrDefaultAsync(ct);
        LatestBankMessage? latest = null;
        if (bank is not null)
        {
            var run = await db.MessageParseRuns.AsNoTracking().SingleAsync(r => r.Id == bank.LatestParseRunId, ct);
            string? path = null, forwarder = null;
            if (run.TrustEvidence is { } json)
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.TryGetProperty("evidence", out var evidence))
                {
                    path = evidence.TryGetProperty("origin_path", out var p) ? p.GetString() : null;
                    forwarder = evidence.TryGetProperty("forwarding_source", out var f) ? f.GetString() : null;
                }
            }

            latest = new LatestBankMessage(bank.Id, bank.ReceivedAt,
                bank.LatestParseOutcome is { } outcome ? SnakeCaseEnum<ParseOutcome>.ToName(outcome) : null,
                run.TrustDecision is { } decision ? SnakeCaseEnum<TrustDecision>.ToName(decision) : null,
                path, forwarder);
        }

        var confirmationView = confirmation is null ? null : ToConfirmation(confirmation);
        var status = address switch
        {
            null => "address_not_created",
            { Processing: InboundAddressProcessing.PausedForeignMail } => "paused_foreign_mail",
            _ when confirmationView is not null && (latest is null || confirmationView.ReceivedAt > latest.ReceivedAt) => "confirmation_received",
            _ when latest?.TrustDecision == "trusted" => "bank_message_trusted",
            _ when latest is not null => "bank_message_untrusted",
            _ => "waiting_for_first_message",
        };

        return new ReceptionStatus(status, address, foreign.Count,
            foreign.OfType<string>().Distinct().Take(10).ToList(), confirmationView, latest, sources);
    }

    private static ConfirmationView ToConfirmation(InboundMessage m)
    {
        var text = m.TextBody is { } body ? HtmlText.Lines(body) : m.HtmlBody is { } html ? HtmlText.ToText(html) : "";
        var code = ConfirmationCode().Match(text) is { Success: true } match ? match.Groups["code"].Value : null;
        return new ConfirmationView(m.Id, m.ReceivedAt, m.SenderDomain, code, text.Length <= 1000 ? text : text[..1000]);
    }

    private IQueryable<ForwardingSource> SourcesOf(Guid connectionId) =>
        db.ForwardingSources.AsNoTracking()
            .Where(s => s.BankConnectionId == connectionId && s.State != ForwardingSourceState.Removed)
            .OrderBy(s => s.CreatedAt);

    /// <summary>Pairrot's own addresses and the bank's identities cannot be forwarding mailboxes.</summary>
    private async Task<ServiceError?> IsReservedAsync(string address, BankConnection connection, CancellationToken ct)
    {
        var domain = address[(address.LastIndexOf('@') + 1)..];
        var bank = CzechBankAccount.ParseIban(connection.Iban).Account is { } account ? profiles.For(account) : null;
        if (bank?.Senders.Allows(address, domain) == true || await db.InboundAddresses.AnyAsync(a => a.Address == address, ct))
        {
            return ServiceError.Validation("invalid_forwarding_address", "address",
                "Enter the mailbox that forwards the notifications, not the bank's or Pairrot's address.");
        }

        return null;
    }

    private async Task<(Tenant? Owner, BankConnection? Connection)> FindAsync(
        Guid callerTenantId, Guid? subtenantId, Guid connectionId, CancellationToken ct)
    {
        var owner = subtenantId is { } childId
            ? await tenants.FindSubtenantAsync(callerTenantId, childId, ct)
            : await tenants.FindAsync(callerTenantId, ct);
        if (owner is null)
        {
            return (null, null);
        }

        var connection = await db.BankConnections.AsNoTracking().SingleOrDefaultAsync(b => b.Id == connectionId && b.TenantId == owner.Id, ct);
        return (owner, connection);
    }

    private static ServiceError? CheckOwner(Tenant owner) => owner.Status switch
    {
        TenantStatus.Deleted => TenantService.TenantDeleted(owner),
        TenantStatus.Suspended => ServiceError.Conflict("tenant_not_active", "The tenant is suspended; reactivate it first."),
        _ => null,
    };

    private static ServiceError? Changeable(Tenant owner, BankConnection connection) =>
        CheckOwner(owner) ?? (connection.Status == BankConnectionStatus.Archived
            ? ServiceError.Conflict("bank_connection_archived", "The bank connection is archived.")
            : null);

    private async Task<ServiceError?> SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return ServiceError.Conflict("concurrent_modification", "The forwarding mailbox changed concurrently; retry the request.");
        }
    }

    private DateTimeOffset Now()
    {
        var now = time.GetUtcNow();
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMicrosecond));
    }

    [GeneratedRegex(@"(?i)(confirmation code|potvrzovací kód|ověřovací kód|kód pro potvrzení)\D{0,10}(?<code>\d{4,12})")]
    private static partial Regex ConfirmationCode();
}
