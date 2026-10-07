using Microsoft.EntityFrameworkCore;
using Pairrot.Core.Inbound;
using Pairrot.Core.Jobs;
using Pairrot.Core.Payments;
using Pairrot.Core.Tenants;
using Pairrot.Core.Transactions;
using Pairrot.Infrastructure.Inbound;
using Pairrot.Infrastructure.Jobs;
using Pairrot.Infrastructure.Persistence;
using Pairrot.Infrastructure.Tenants;

namespace Pairrot.Infrastructure.Transactions;

public sealed record TransactionFilter(
    Guid? BankConnectionId = null,
    DateOnly? BookedFrom = null,
    DateOnly? BookedTo = null,
    long? Amount = null,
    string? VariableSymbol = null,
    IngestionStatus? IngestionStatus = null);

/// <summary>A transaction with the messages supporting it, oldest link first.</summary>
public sealed record TransactionDetail(IncomingTransaction Transaction, IReadOnlyList<TransactionSource> Sources);

/// <summary>A message's parse history: completed runs and the parse jobs behind them, oldest first.</summary>
public sealed record ParseHistory(InboundMessage Message, IReadOnlyList<MessageParseRun> Runs, IReadOnlyList<BackgroundJob> Jobs);

public sealed record QueuedParse(BackgroundJob Job, bool Created);

/// <summary>
/// Incoming transactions and parse history as their owners see them: the caller's tenant or,
/// for parent management, a direct child. Foreign records look missing. Nothing here changes a
/// payment request.
/// </summary>
public class IncomingTransactionService(PairrotDbContext db, TenantService tenants, BackgroundJobQueue jobs)
{
    public async Task<ServiceResult<Page<IncomingTransaction>>> ListAsync(
        Guid callerTenantId, Guid? subtenantId, TransactionFilter filter, int limit, Guid? before, CancellationToken ct = default)
    {
        if (await FindOwnerAsync(callerTenantId, subtenantId, ct) is not { } owner)
        {
            return ServiceError.NotFound();
        }

        var query = db.IncomingTransactions.AsNoTracking().Where(t => t.TenantId == owner.Id);
        if (filter.BankConnectionId is { } connectionId)
        {
            query = query.Where(t => t.BankConnectionId == connectionId);
        }

        if (filter.BookedFrom is { } from)
        {
            query = query.Where(t => t.BookedOn >= from);
        }

        if (filter.BookedTo is { } to)
        {
            query = query.Where(t => t.BookedOn <= to);
        }

        if (filter.Amount is { } amount)
        {
            query = query.Where(t => t.Amount == amount);
        }

        if (filter.VariableSymbol is { } symbol)
        {
            // The same leading-zero rule as payment requests.
            var key = PaymentRequestRules.VariableSymbolKey(symbol);
            query = query.Where(t => t.VariableSymbolKey == key);
        }

        if (filter.IngestionStatus is { } status)
        {
            query = query.Where(t => t.IngestionStatus == status);
        }

        if (before is { } cursor)
        {
            query = query.Where(t => t.Id.CompareTo(cursor) < 0);
        }

        var items = await query.OrderByDescending(t => t.Id).Take(limit + 1).ToListAsync(ct);
        var hasMore = items.Count > limit;
        if (hasMore)
        {
            items.RemoveAt(items.Count - 1);
        }

        return new Page<IncomingTransaction>(items, hasMore ? items[^1].Id : null);
    }

    public async Task<ServiceResult<TransactionDetail>> GetAsync(
        Guid callerTenantId, Guid? subtenantId, Guid transactionId, CancellationToken ct = default)
    {
        if (await FindOwnerAsync(callerTenantId, subtenantId, ct) is not { } owner)
        {
            return ServiceError.NotFound();
        }

        var transaction = await db.IncomingTransactions.AsNoTracking()
            .SingleOrDefaultAsync(t => t.Id == transactionId && t.TenantId == owner.Id, ct);
        if (transaction is null)
        {
            return ServiceError.NotFound();
        }

        var sources = await db.TransactionSources.AsNoTracking()
            .Where(s => s.TransactionId == transaction.Id && s.TenantId == owner.Id)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(ct);
        return new TransactionDetail(transaction, sources);
    }

    public async Task<ServiceResult<ParseHistory>> ListParseRunsAsync(
        Guid callerTenantId, Guid? subtenantId, Guid messageId, CancellationToken ct = default)
    {
        var (_, message) = await FindMessageAsync(callerTenantId, subtenantId, messageId, ct);
        if (message is null)
        {
            return ServiceError.NotFound();
        }

        var runs = await db.MessageParseRuns.AsNoTracking()
            .Where(r => r.InboundMessageId == message.Id && r.TenantId == message.TenantId)
            .OrderBy(r => r.JobGeneration)
            .ToListAsync(ct);
        var parseJobs = await db.BackgroundJobs.AsNoTracking()
            .Where(j => j.Type == BackgroundJobTypes.ParseInboundMessage && j.TargetId == message.Id)
            .OrderBy(j => j.Generation)
            .ToListAsync(ct);
        return new ParseHistory(message, runs, parseJobs);
    }

    /// <summary>
    /// Queues a new parse generation with the installed profile, or returns the one still queued
    /// or running. Clients choose nothing about the result: no parser, trust, amount, or owner.
    /// </summary>
    public async Task<ServiceResult<QueuedParse>> ReprocessAsync(
        Guid callerTenantId, Guid? subtenantId, Guid messageId, Actor actor, CancellationToken ct = default)
    {
        var (owner, message) = await FindMessageAsync(callerTenantId, subtenantId, messageId, ct);
        if (message is null)
        {
            return ServiceError.NotFound();
        }

        if (owner!.Status == TenantStatus.Deleted)
        {
            return ServiceError.Conflict("tenant_deleted", "The tenant is deleted; its messages are history only.");
        }

        if (await ParseEligibility.HeldAsync(db, message, ct) is { } held)
        {
            return ServiceError.Conflict(held.Code, held.Message);
        }

        return await QueueAsync(message, InboundFetchQueue.RequesterOf(actor), actor, ct);
    }

    /// <summary>Operator reprocessing (CLI); the same eligibility rules, audited as the operator.</summary>
    public async Task<ServiceResult<QueuedParse>> ReprocessAsOperatorAsync(Guid messageId, Actor actor, CancellationToken ct = default)
    {
        var message = await db.InboundMessages.AsNoTracking().SingleOrDefaultAsync(m => m.Id == messageId, ct);
        if (message is null)
        {
            return ServiceError.NotFound();
        }

        if (await ParseEligibility.HeldAsync(db, message, ct) is { } held)
        {
            return ServiceError.Conflict(held.Code, held.Message);
        }

        return await QueueAsync(message, InboundFetchQueue.RequesterOf(actor), actor, ct);
    }

    private async Task<ServiceResult<QueuedParse>> QueueAsync(InboundMessage message, string requestedBy, Actor actor, CancellationToken ct)
    {
        var (job, created) = await jobs.EnqueueAsync(BackgroundJobTypes.ParseInboundMessage, message.Id, requestedBy, () =>
        {
            tenants.AddAudit(actor, message.TenantId!.Value, AuditActions.InboundMessageReprocessRequested, new
            {
                MessageId = message.Id,
                ConnectionId = message.BankConnectionId,
            });
            return Task.CompletedTask;
        }, ct);
        return new QueuedParse(job, created);
    }

    private Task<Tenant?> FindOwnerAsync(Guid callerTenantId, Guid? subtenantId, CancellationToken ct) =>
        subtenantId is { } childId
            ? tenants.FindSubtenantAsync(callerTenantId, childId, ct)
            : tenants.FindAsync(callerTenantId, ct);

    private async Task<(Tenant? Owner, InboundMessage? Message)> FindMessageAsync(
        Guid callerTenantId, Guid? subtenantId, Guid messageId, CancellationToken ct)
    {
        if (await FindOwnerAsync(callerTenantId, subtenantId, ct) is not { } owner)
        {
            return (null, null);
        }

        var message = await db.InboundMessages.AsNoTracking().SingleOrDefaultAsync(m => m.Id == messageId && m.TenantId == owner.Id, ct);
        return (owner, message);
    }
}
