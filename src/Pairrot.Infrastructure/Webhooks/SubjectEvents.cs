using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Pairrot.Core.Inbound;
using Pairrot.Core.Matching;
using Pairrot.Core.Parsing;
using Pairrot.Core.Transactions;
using Pairrot.Core.Webhooks;
using Pairrot.Infrastructure.Erasure;
using Pairrot.Infrastructure.Persistence;
using Causes = Pairrot.Core.Webhooks.WebhookEvents.Causes;

namespace Pairrot.Infrastructure.Webhooks;

/// <summary>
/// Feature 016: records the events of transactions, review cases, and inbound addresses from the
/// changes being saved, so every code path that changes them produces its event in the same
/// database transaction. A save without its own transaction gets one for the event's locks.
/// </summary>
public sealed class SubjectEventInterceptor(TimeProvider time) : SaveChangesInterceptor
{
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is PairrotDbContext db)
        {
            await new SubjectEventRecorder(db, Now()).RecordAsync(cancellationToken);
        }

        return result;
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is PairrotDbContext db)
        {
            new SubjectEventRecorder(db, Now()).RecordAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        return result;
    }

    public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is PairrotDbContext { EventTransaction: { } transaction } db)
        {
            db.EventTransaction = null;
            await transaction.CommitAsync(cancellationToken);
            await transaction.DisposeAsync();
        }

        return result;
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        if (eventData.Context is PairrotDbContext { EventTransaction: { } transaction } db)
        {
            db.EventTransaction = null;
            transaction.Commit();
            transaction.Dispose();
        }

        return result;
    }

    public override async Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default) =>
        await AbortAsync(eventData.Context);

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => AbortAsync(eventData.Context).GetAwaiter().GetResult();

    public override async Task SaveChangesCanceledAsync(DbContextEventData eventData, CancellationToken cancellationToken = default) =>
        await AbortAsync(eventData.Context);

    private static async Task AbortAsync(DbContext? context)
    {
        if (context is PairrotDbContext { EventTransaction: { } transaction } db)
        {
            db.EventTransaction = null;
            await transaction.DisposeAsync();
        }
    }

    private DateTimeOffset Now()
    {
        var now = time.GetUtcNow();
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMicrosecond));
    }
}

/// <summary>
/// One save's events. Each subject gets at most one event per save, with the next sequence of its
/// subject; the first event of a transaction is <c>transaction.recorded</c>. Nothing is recorded
/// for an owner behind Feature 014's barrier. Payloads carry no sender data, content, or reasons.
/// </summary>
internal sealed class SubjectEventRecorder(PairrotDbContext db, DateTimeOffset now)
{
    private readonly Dictionary<Guid, bool> _closed = [];

    public async Task RecordAsync(CancellationToken ct)
    {
        db.ChangeTracker.DetectChanges();
        var entries = db.ChangeTracker.Entries().ToList();
        var transactions = Changed<IncomingTransaction>(entries);
        var cases = Changed<PaymentReviewCase>(entries);
        var addresses = Changed<InboundAddress>(entries)
            .Where(e => e.State == EntityState.Modified && e.Property(a => a.Processing).IsModified
                && !Equals(e.Property(a => a.Processing).OriginalValue, e.Entity.Processing))
            .ToList();
        var members = entries.Where(e => e.State == EntityState.Added).Select(e => e.Entity).OfType<PaymentReviewCaseMember>().ToList();
        if (transactions.Count + cases.Count + addresses.Count + members.Count == 0)
        {
            return;
        }

        // An event already waiting in this context (a retried save) is not recorded twice.
        var pending = entries.Where(e => e.State == EntityState.Added).Select(e => e.Entity).OfType<WebhookEvent>()
            .Select(e => e.SubjectId).OfType<Guid>().ToHashSet();
        if (db.Database.CurrentTransaction is null)
        {
            db.EventTransaction = await db.Database.BeginTransactionAsync(ct);
        }

        var joined = members.Select(m => m.TransactionId).OfType<Guid>().ToHashSet();
        foreach (var id in joined.Where(id => transactions.All(e => e.Entity.Id != id)).Order())
        {
            if (await db.IncomingTransactions.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, ct) is { } unchanged)
            {
                transactions.Add(db.Entry(unchanged));
            }
        }

        foreach (var entry in transactions.Where(e => !pending.Contains(e.Entity.Id)).OrderBy(e => e.Entity.Id))
        {
            await TransactionEventAsync(entry, joined.Contains(entry.Entity.Id), ct);
        }

        var extended = members.Select(m => m.CaseId).ToHashSet();
        foreach (var id in extended.Where(id => cases.All(e => e.Entity.Id != id)).Order())
        {
            if (await db.ReviewCases.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct) is { } unchanged)
            {
                cases.Add(db.Entry(unchanged));
            }
        }

        foreach (var entry in cases.Where(e => !pending.Contains(e.Entity.Id)).OrderBy(e => e.Entity.Id))
        {
            await ReviewCaseEventAsync(entry, members.Where(m => m.CaseId == entry.Entity.Id).ToList(), ct);
        }

        foreach (var entry in addresses.Where(e => !pending.Contains(e.Entity.Id)).OrderBy(e => e.Entity.Id))
        {
            await AddressEventAsync(entry, ct);
        }
    }

    private List<EntityEntry<T>> Changed<T>(List<EntityEntry> entries) where T : class =>
        entries.Where(e => e.Entity is T && e.State is EntityState.Added or EntityState.Modified).Select(e => db.Entry((T)e.Entity)).ToList();

    private async Task TransactionEventAsync(EntityEntry<IncomingTransaction> entry, bool joinedCase, CancellationToken ct)
    {
        var t = entry.Entity;
        var last = await LastSequenceAsync(t.Id, ct);
        string type, cause;
        if (last == 0)
        {
            // Recorded once its first matching attempt completed, or when it cannot be matched automatically.
            var ready = t.MatchCheckedAt is not null || t.IngestionStatus != IngestionStatus.Accepted || t.MovementConflict
                || t.Handling != TransactionHandling.Unallocated || joinedCase;
            if (!ready)
            {
                return;
            }

            (type, cause) = (WebhookEvents.TransactionRecorded, Causes.Recorded);
        }
        else if ((Hint(t.Id) ?? TransactionCause(entry, joinedCase)) is { } changed)
        {
            (type, cause) = (WebhookEvents.TransactionUpdated, changed);
        }
        else
        {
            return;
        }

        if (await ClosedAsync(t.TenantId, ct))
        {
            return;
        }

        var allocation = await ActiveAllocationAsync(t.Id, ct);
        var requestId = allocation?.PaymentRequestId ?? t.MatchRequestId;
        var externalId = requestId is { } rid
            ? db.PaymentRequests.Local.FirstOrDefault(r => r.Id == rid)?.ExternalId
                ?? await db.PaymentRequests.AsNoTracking().Where(r => r.Id == rid).Select(r => r.ExternalId).SingleOrDefaultAsync(ct)
            : null;
        var matching = new { result = t.MatchResult, payment_request_id = requestId, external_id = externalId };
        var sequence = last + 1;
        object data = type == WebhookEvents.TransactionRecorded
            ? new
            {
                subject_id = t.Id, sequence, cause,
                transaction_id = t.Id, bank_connection_id = t.BankConnectionId, bank_account_id = t.BankAccountId,
                amount = t.Amount, currency = t.Currency, variable_symbol = t.VariableSymbol, booked_on = t.BookedOn,
                occurred_at = t.OccurredAt, origin_path = SnakeCaseEnum<OriginPath>.ToName(t.OriginPath),
                handling_state = SnakeCaseEnum<TransactionHandling>.ToName(t.Handling), matching,
            }
            : new
            {
                subject_id = t.Id, sequence, cause,
                transaction_id = t.Id, bank_connection_id = t.BankConnectionId, bank_account_id = t.BankAccountId,
                amount = t.Amount, currency = t.Currency, variable_symbol = t.VariableSymbol, booked_on = t.BookedOn,
                occurred_at = t.OccurredAt, origin_path = SnakeCaseEnum<OriginPath>.ToName(t.OriginPath),
                handling_state = SnakeCaseEnum<TransactionHandling>.ToName(t.Handling), matching, allocation_id = allocation?.Id,
            };
        await AddAsync(type, t.TenantId, t.Id, sequence, MatchingRules.IsLive(t) ? InboundEnvironment.Live : InboundEnvironment.Test, data, ct);
    }

    /// <summary>Why an already announced transaction changed, or <c>null</c> when nothing a receiver sees changed.</summary>
    private string? TransactionCause(EntityEntry<IncomingTransaction> entry, bool joinedCase)
    {
        var t = entry.Entity;
        if (entry.State != EntityState.Modified)
        {
            return joinedCase ? Causes.ReviewRequired : null;
        }

        T Original<T>(string name) => (T)entry.Property(name).OriginalValue!;
        var handling = Original<TransactionHandling>(nameof(IncomingTransaction.Handling));
        if (handling != t.Handling)
        {
            return t.Handling switch
            {
                TransactionHandling.Allocated => Causes.Allocated,
                TransactionHandling.Ignored => Causes.Ignored,
                TransactionHandling.Duplicate => Causes.MarkedDuplicate,
                TransactionHandling.Rejected => Causes.Rejected,
                _ when handling == TransactionHandling.Ignored => Causes.Restored,
                _ when handling == TransactionHandling.Allocated => RevokedForReview(t.Id) || joinedCase ? Causes.ReviewRequired : Causes.Revoked,
                _ => Causes.ReviewDecided,
            };
        }

        var ingestion = Original<IngestionStatus>(nameof(IncomingTransaction.IngestionStatus));
        var confirmed = Original<bool>(nameof(IncomingTransaction.ManuallyConfirmed));
        var conflict = Original<bool>(nameof(IncomingTransaction.MovementConflict));
        if (joinedCase || (ingestion != t.IngestionStatus && t.IngestionStatus != IngestionStatus.Accepted)
            || (!conflict && t.MovementConflict) || (confirmed && !t.ManuallyConfirmed))
        {
            return Causes.ReviewRequired;
        }

        if ((!confirmed && t.ManuallyConfirmed) || (conflict && !t.MovementConflict))
        {
            return Causes.ReviewDecided;
        }

        if (Original<bool>(nameof(IncomingTransaction.AutoMatchBlocked)) && !t.AutoMatchBlocked)
        {
            return Causes.AutoMatchingEnabled;
        }

        return Original<string?>(nameof(IncomingTransaction.MatchResult)) != t.MatchResult
            || Original<Guid?>(nameof(IncomingTransaction.MatchRequestId)) != t.MatchRequestId
                ? Causes.Rematched
                : null;
    }

    /// <summary>The allocation was revoked in this save because the transaction lost its eligibility.</summary>
    private bool RevokedForReview(Guid transactionId) =>
        db.ChangeTracker.Entries<PaymentAllocation>().Any(e => e.Entity.TransactionId == transactionId
            && e.State == EntityState.Modified && e.Entity.State == AllocationState.Revoked
            && e.Entity.RevocationCode == RevocationCodes.TransactionReviewRequired);

    private async Task<PaymentAllocation?> ActiveAllocationAsync(Guid transactionId, CancellationToken ct)
    {
        var tracked = db.ChangeTracker.Entries<PaymentAllocation>().Where(e => e.Entity.TransactionId == transactionId).Select(e => e.Entity).ToList();
        if (tracked.FirstOrDefault(a => a.State == AllocationState.Active) is { } active)
        {
            return active;
        }

        var ids = tracked.Select(a => a.Id).ToList();
        return await db.PaymentAllocations.AsNoTracking()
            .Where(a => a.TransactionId == transactionId && a.State == AllocationState.Active && !ids.Contains(a.Id))
            .SingleOrDefaultAsync(ct);
    }

    private async Task ReviewCaseEventAsync(EntityEntry<PaymentReviewCase> entry, List<PaymentReviewCaseMember> added, CancellationToken ct)
    {
        var c = entry.Entity;
        string type, cause;
        if (entry.State == EntityState.Added)
        {
            (type, cause) = (WebhookEvents.ReviewCaseOpened, Causes.Opened);
        }
        else if ((Hint(c.Id) ?? CaseCause(entry, added.Count > 0)) is { } changed)
        {
            (type, cause) = (WebhookEvents.ReviewCaseUpdated, changed);
        }
        else
        {
            return;
        }

        if (await ClosedAsync(c.TenantId, ct))
        {
            return;
        }

        var stored = await db.ReviewCaseMembers.AsNoTracking().Where(m => m.CaseId == c.Id).ToListAsync(ct);
        var all = stored.Concat(added).ToList();
        var transactionIds = all.Select(m => m.TransactionId).OfType<Guid>().Distinct().Order().ToList();
        var runIds = all.Select(m => m.ParseRunId).OfType<Guid>().Distinct().ToList();
        var live = await LiveEvidenceAsync(transactionIds, runIds, c.InboundMessageId, ct);
        var decision = db.ChangeTracker.Entries<PaymentReviewDecision>()
            .Where(e => e.State == EntityState.Added && e.Entity.CaseId == c.Id)
            .Select(e => e.Entity).OrderBy(d => d.CreatedAt).ThenBy(d => d.Id).LastOrDefault();
        var sequence = await LastSequenceAsync(c.Id, ct) + 1;
        var issue = SnakeCaseEnum<ReviewIssue>.ToName(c.Issue);
        var state = SnakeCaseEnum<ReviewCaseState>.ToName(c.State);
        object data = type == WebhookEvents.ReviewCaseOpened
            ? new
            {
                subject_id = c.Id, sequence, cause,
                review_case_id = c.Id, bank_connection_id = c.BankConnectionId, issue, state, reason_code = c.ReasonCode,
                transaction_ids = transactionIds,
            }
            : new
            {
                subject_id = c.Id, sequence, cause,
                review_case_id = c.Id, bank_connection_id = c.BankConnectionId, issue, state, reason_code = c.ReasonCode,
                transaction_ids = transactionIds,
                resolution = c.State == ReviewCaseState.Resolved && c.Resolution is { } resolution ? SnakeCaseEnum<ReviewAction>.ToName(resolution) : null,
                decision_id = decision?.Id,
            };
        await AddAsync(type, c.TenantId, c.Id, sequence, live ? InboundEnvironment.Live : InboundEnvironment.Test, data, ct);
    }

    private static string? CaseCause(EntityEntry<PaymentReviewCase> entry, bool membersAdded)
    {
        var c = entry.Entity;
        if (entry.State == EntityState.Modified)
        {
            var state = (ReviewCaseState)entry.Property(nameof(PaymentReviewCase.State)).OriginalValue!;
            if (state != c.State)
            {
                return c.State == ReviewCaseState.Resolved ? Causes.Resolved : Causes.Reopened;
            }

            if ((string)entry.Property(nameof(PaymentReviewCase.EvidenceKey)).OriginalValue! != c.EvidenceKey)
            {
                return Causes.EvidenceChanged;
            }
        }

        return membersAdded ? Causes.MemberAdded : null;
    }

    /// <summary>A case is live evidence only when every transaction and message it holds is (Feature 016: test cases reach test endpoints only).</summary>
    private async Task<bool> LiveEvidenceAsync(List<Guid> transactionIds, List<Guid> runIds, Guid? messageId, CancellationToken ct)
    {
        foreach (var id in transactionIds)
        {
            var t = db.IncomingTransactions.Local.FirstOrDefault(x => x.Id == id)
                ?? await db.IncomingTransactions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (t is not null && !MatchingRules.IsLive(t))
            {
                return false;
            }
        }

        var messageIds = db.MessageParseRuns.Local.Where(r => runIds.Contains(r.Id)).Select(r => r.InboundMessageId).ToList();
        messageIds.AddRange(await db.MessageParseRuns.AsNoTracking().Where(r => runIds.Contains(r.Id)).Select(r => r.InboundMessageId).ToListAsync(ct));
        if (messageId is { } own)
        {
            messageIds.Add(own);
        }

        var ids = messageIds.Distinct().ToList();
        return !await db.InboundMessages.AsNoTracking()
            .AnyAsync(m => ids.Contains(m.Id) && (m.SourceKind != InboundSourceKind.Received || m.Environment != InboundEnvironment.Live), ct);
    }

    private async Task AddressEventAsync(EntityEntry<InboundAddress> entry, CancellationToken ct)
    {
        var a = entry.Entity;
        if (await ClosedAsync(a.TenantId, ct))
        {
            return;
        }

        var paused = a.Processing == InboundAddressProcessing.PausedForeignMail;
        var type = paused ? WebhookEvents.InboundAddressPaused : WebhookEvents.InboundAddressResumed;
        var cause = Hint(a.Id) ?? (paused ? Causes.ForeignMailLimit : Causes.ResumedByTenant);
        var since = now.AddHours(-24);
        var foreign = await db.InboundMessages.AsNoTracking()
            .CountAsync(m => m.InboundAddressId == a.Id && m.SenderClass == InboundSenderClass.Foreign && m.CreatedAt >= since, ct)
            + db.ChangeTracker.Entries<InboundMessage>().Count(e => e.State == EntityState.Added && e.Entity.InboundAddressId == a.Id
                && e.Entity.SenderClass == InboundSenderClass.Foreign);
        var sequence = await LastSequenceAsync(a.Id, ct) + 1;
        var data = new
        {
            subject_id = a.Id, sequence, cause,
            inbound_address_id = a.Id, bank_connection_id = a.BankConnectionId,
            address_processing_state = SnakeCaseEnum<InboundAddressProcessing>.ToName(a.Processing),
            foreign_count_24h = foreign,
            operator_only_resume = a.OperatorResumeRequired,
        };
        await AddAsync(type, a.TenantId, a.Id, sequence, a.Environment, data, ct);
    }

    private async Task AddAsync(string type, Guid owner, Guid subject, int sequence, InboundEnvironment environment, object data, CancellationToken ct)
    {
        var ev = WebhookEvents.ForSubject(Guid.CreateVersion7(), type, owner, subject, sequence, environment, now, data);
        db.WebhookEvents.Add(ev);
        await WebhookRouter.DeliverAsync(db, ev, ct);
    }

    private async Task<int> LastSequenceAsync(Guid subjectId, CancellationToken ct) =>
        await db.WebhookEvents.AsNoTracking().Where(e => e.SubjectId == subjectId).MaxAsync(e => e.Sequence, ct) ?? 0;

    /// <summary>Feature 014: an owner behind the barrier gets no new payloads.</summary>
    private async Task<bool> ClosedAsync(Guid tenantId, CancellationToken ct)
    {
        if (!_closed.TryGetValue(tenantId, out var closed))
        {
            closed = _closed[tenantId] = await DataBarrier.IsClosedAsync(db, tenantId, ct);
        }

        return closed;
    }

    private string? Hint(Guid subjectId) => db.EventCauses.Remove(subjectId, out var cause) ? cause : null;
}
