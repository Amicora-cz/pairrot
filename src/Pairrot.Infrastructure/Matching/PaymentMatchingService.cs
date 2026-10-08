using Microsoft.EntityFrameworkCore;
using Pairrot.Core.Jobs;
using Pairrot.Core.Matching;
using Pairrot.Core.Payments;
using Pairrot.Core.Tenants;
using Pairrot.Core.Transactions;
using Pairrot.Infrastructure.Inbound;
using Pairrot.Infrastructure.Jobs;
using Pairrot.Infrastructure.Persistence;
using Pairrot.Infrastructure.Tenants;

namespace Pairrot.Infrastructure.Matching;

/// <summary>What Feature 009 says about a transaction now.</summary>
/// <param name="ManualBlock">Why no one can allocate it now, or <c>null</c>.</param>
/// <param name="AutomaticBlock">Why automatic matching skips it, or <c>null</c>.</param>
public sealed record TransactionMatching(
    PaymentAllocation? Allocation, IReadOnlyList<Guid> OpenReviewCaseIds, string? ManualBlock, string? AutomaticBlock);

public sealed record TransactionView(IncomingTransaction Transaction, TransactionMatching Matching);

public sealed record AllocationOutcome(PaymentAllocation Allocation, TransactionView Transaction, PaymentRequest Request);

/// <param name="Reasons">E.g. <c>variable_symbol_match</c>, <c>amount_mismatch</c>, <c>request_already_paid</c>.</param>
/// <param name="Proposed">The request's result if this transaction were allocated to it; <c>null</c> when it cannot be.</param>
/// <param name="Block">Why it cannot be allocated to this request, or <c>null</c>.</param>
public sealed record MatchingCandidate(PaymentRequest Request, IReadOnlyList<string> Reasons, PaymentSummary? Proposed, string? Block);

public sealed record CandidateList(TransactionView Transaction, IReadOnlyList<MatchingCandidate> Candidates);

/// <summary>Manual search within the transaction's owner, account (any registration), and currency; all given values must match.</summary>
public sealed record CandidateSearch(string? ExternalId = null, string? VariableSymbol = null, long? Amount = null)
{
    public bool IsEmpty => ExternalId is null && VariableSymbol is null && Amount is null;
}

public sealed record AllocateInput(Guid PaymentRequestId, int? TransactionVersion, int? RequestVersion, string? Reason);

public sealed record RevokeInput(int? AllocationVersion, int? TransactionVersion, int? RequestVersion, string? Reason);

public sealed record HandlingInput(int? Version, string? Reason);

/// <summary>
/// Manual matching for one owner (the caller's tenant or a direct child): candidates, allocation,
/// revocation, ignoring, restoring, enabling automatic matching, and the request's allocation and
/// change history. Writes are idempotent commands (<see cref="ManualCommands"/>) that recheck
/// every rule under the account lock and require the versions the caller saw.
/// </summary>
public class PaymentMatchingService(
    PairrotDbContext db, TenantService tenants, PaymentLedger ledger, ManualCommands commands, BackgroundJobQueue jobs)
{
    private const int MaxCandidates = 50;

    public DateTimeOffset Now() => ledger.Now();

    /// <summary>Allocation, open review cases, and blocks for each transaction.</summary>
    public async Task<IReadOnlyDictionary<Guid, TransactionMatching>> DescribeAsync(
        IReadOnlyCollection<IncomingTransaction> transactions, CancellationToken ct)
    {
        var ids = transactions.Select(t => t.Id).ToList();
        var allocations = await db.PaymentAllocations.AsNoTracking()
            .Where(a => ids.Contains(a.TransactionId) && a.State == AllocationState.Active)
            .ToDictionaryAsync(a => a.TransactionId, ct);
        var cases = (await db.ReviewCaseMembers.AsNoTracking()
                .Where(m => m.TransactionId != null && ids.Contains(m.TransactionId.Value)
                    && db.ReviewCases.Any(c => c.Id == m.CaseId && c.State == ReviewCaseState.Open))
                .Select(m => new { TransactionId = m.TransactionId!.Value, m.CaseId })
                .ToListAsync(ct))
            .GroupBy(m => m.TransactionId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Guid>)g.Select(m => m.CaseId).Distinct().Order().ToList());

        return transactions.ToDictionary(t => t.Id, t =>
        {
            var open = cases.GetValueOrDefault(t.Id) ?? [];
            return new TransactionMatching(
                allocations.GetValueOrDefault(t.Id),
                open,
                MatchingRules.ManualBlock(t, open.Count > 0, ledger.AllowTestEvidence),
                MatchingRules.AutomaticBlock(t, open.Count > 0, ledger.AllowTestEvidence));
        });
    }

    public async Task<TransactionView> ViewAsync(IncomingTransaction transaction, CancellationToken ct) =>
        new(transaction, (await DescribeAsync([transaction], ct))[transaction.Id]);

    public async Task<ServiceResult<CandidateList>> GetCandidatesAsync(
        Guid callerTenantId, Guid? subtenantId, Guid transactionId, CandidateSearch search, CancellationToken ct = default)
    {
        if (await FindTransactionAsync(callerTenantId, subtenantId, transactionId, ct) is not ({ } owner, { } transaction))
        {
            return ServiceError.NotFound();
        }

        var view = await ViewAsync(transaction, ct);
        var scope = db.PaymentRequests.AsNoTracking().Where(r =>
            r.TenantId == owner.Id && r.BankAccountId == transaction.BankAccountId && r.Currency == transaction.Currency);

        List<PaymentRequest> found;
        if (search.IsEmpty)
        {
            var bySymbol = transaction.VariableSymbolKey is { } key
                ? await scope.Where(r => r.VariableSymbolKey == key).ToListAsync(ct)
                : [];
            var byAmount = await scope
                .Where(r => r.Amount == transaction.Amount && (r.Status == PaymentRequestStatus.Open || r.Status == PaymentRequestStatus.PartiallyPaid))
                .OrderByDescending(r => r.Id)
                .Take(MaxCandidates)
                .ToListAsync(ct);
            found = bySymbol.Concat(byAmount).DistinctBy(r => r.Id).Take(MaxCandidates).ToList();
        }
        else
        {
            var query = scope;
            if (search.ExternalId is { } externalId)
            {
                query = query.Where(r => r.ExternalId == externalId);
            }

            if (search.VariableSymbol is { } symbol)
            {
                var symbolKey = PaymentRequestRules.VariableSymbolKey(symbol);
                query = query.Where(r => r.VariableSymbolKey == symbolKey);
            }

            if (search.Amount is { } amount)
            {
                query = query.Where(r => r.Amount == amount);
            }

            found = await query.OrderByDescending(r => r.Id).Take(MaxCandidates).ToListAsync(ct);
        }

        var candidates = found.Select(r => Candidate(transaction, r, view.Matching.ManualBlock)).ToList();
        return new CandidateList(view, candidates);
    }

    /// <summary>Queues a matching attempt, or returns the one still queued. Review and manual blocks stay effective.</summary>
    public async Task<ServiceResult<(BackgroundJob Job, bool Created)>> QueueMatchAsync(
        Guid callerTenantId, Guid? subtenantId, Guid transactionId, Actor actor, CancellationToken ct = default)
    {
        if (await FindTransactionAsync(callerTenantId, subtenantId, transactionId, ct) is not ({ } owner, { } transaction))
        {
            return ServiceError.NotFound();
        }

        if (await ledger.LifecycleBlockAsync(owner.Id, transaction.BankConnectionId, ct) is { } lifecycle)
        {
            return LifecycleError(lifecycle);
        }

        return await jobs.EnqueueAsync(BackgroundJobTypes.MatchTransaction, transaction.Id, InboundFetchQueue.RequesterOf(actor), () =>
        {
            tenants.AddAudit(actor, owner.Id, AuditActions.TransactionMatchRequested, new { TransactionId = transaction.Id });
            return Task.CompletedTask;
        }, ct);
    }

    public async Task<ServiceResult<CommandOutcome>> AllocateAsync(
        Guid callerTenantId, Guid? subtenantId, Guid transactionId, AllocateInput input, CommandRequest command,
        Func<AllocationOutcome, object> render, CancellationToken ct = default)
    {
        if (MatchingRules.ValidateReason(input.Reason) is { } reasonError)
        {
            return ServiceError.Validation("invalid_reason", "reason", reasonError);
        }

        if (await FindTransactionAsync(callerTenantId, subtenantId, transactionId, ct) is not ({ } owner, { } found))
        {
            return ServiceError.NotFound();
        }

        if (input.TransactionVersion is not { } transactionVersion || input.RequestVersion is not { } requestVersion)
        {
            return ServiceError.PreconditionRequired("Send expected_transaction_version and expected_request_version as shown to you.");
        }

        return await commands.RunAsync(owner.Id, found.BankAccountId, command, async () =>
        {
            var transaction = await db.IncomingTransactions.SingleAsync(t => t.Id == found.Id, ct);
            var request = await db.PaymentRequests.SingleOrDefaultAsync(r => r.Id == input.PaymentRequestId && r.TenantId == owner.Id, ct);
            if (request is null)
            {
                return ServiceError.NotFound("The payment request was not found.");
            }

            // Another registration of the same account is fine (Feature 013); another account is not.
            if (request.BankAccountId != transaction.BankAccountId)
            {
                return ServiceError.Conflict("bank_connection_mismatch", "The payment request belongs to a different bank account than the transaction.");
            }

            if (request.Currency != transaction.Currency)
            {
                return ServiceError.Conflict("currency_mismatch", "The payment request is in a different currency than the transaction.");
            }

            if (await ledger.LifecycleBlockAsync(owner.Id, transaction.BankConnectionId, ct) is { } lifecycle)
            {
                return LifecycleError(lifecycle);
            }

            if (transaction.Version != transactionVersion || request.Version != requestVersion)
            {
                return Stale();
            }

            if (MatchingRules.ManualBlock(transaction, await ledger.InOpenCaseAsync(transaction.Id, ct), ledger.AllowTestEvidence) is { } block)
            {
                return BlockError(block);
            }

            if (request.Status == PaymentRequestStatus.Cancelled)
            {
                return ServiceError.Conflict("payment_request_cancelled", "A cancelled payment request cannot receive an allocation.");
            }

            var allocated = await ledger.AllocateAsync(transaction, request, AllocationMethod.Manual, input.Reason!.Trim(), command.Actor, ct);
            if (allocated.Error is { } error)
            {
                return error;
            }

            await db.SaveChangesAsync(ct);
            var outcome = new AllocationOutcome(allocated.Value!, await ViewAsync(transaction, ct), request);
            return new CommandResult(201, render(outcome),
                new { AllocationId = outcome.Allocation.Id, TransactionId = transaction.Id, PaymentRequestId = request.Id });
        }, ct);
    }

    public async Task<ServiceResult<CommandOutcome>> RevokeAsync(
        Guid callerTenantId, Guid? subtenantId, Guid allocationId, RevokeInput input, CommandRequest command,
        Func<AllocationOutcome, object> render, CancellationToken ct = default)
    {
        if (MatchingRules.ValidateReason(input.Reason) is { } reasonError)
        {
            return ServiceError.Validation("invalid_reason", "reason", reasonError);
        }

        if (await FindOwnerAsync(callerTenantId, subtenantId, ct) is not { } owner)
        {
            return ServiceError.NotFound();
        }

        var found = await db.PaymentAllocations.AsNoTracking().SingleOrDefaultAsync(a => a.Id == allocationId && a.TenantId == owner.Id, ct);
        if (found is null)
        {
            return ServiceError.NotFound();
        }

        if (input.AllocationVersion is not { } allocationVersion || input.TransactionVersion is not { } transactionVersion
            || input.RequestVersion is not { } requestVersion)
        {
            return ServiceError.PreconditionRequired(
                "Send expected_allocation_version, expected_transaction_version, and expected_request_version as shown to you.");
        }

        return await commands.RunAsync(owner.Id, found.BankAccountId, command, async () =>
        {
            var allocation = await db.PaymentAllocations.SingleAsync(a => a.Id == found.Id, ct);
            var transaction = await db.IncomingTransactions.SingleAsync(t => t.Id == allocation.TransactionId, ct);
            var request = await db.PaymentRequests.SingleAsync(r => r.Id == allocation.PaymentRequestId, ct);
            if (await ledger.LifecycleBlockAsync(owner.Id, allocation.BankConnectionId, ct) is { } lifecycle)
            {
                return LifecycleError(lifecycle);
            }

            if (allocation.Version != allocationVersion || transaction.Version != transactionVersion || request.Version != requestVersion)
            {
                return Stale();
            }

            if (allocation.State != AllocationState.Active)
            {
                return ServiceError.Conflict("allocation_revoked", "The allocation is already revoked.");
            }

            await ledger.RevokeAsync(allocation, transaction, request, RevocationCodes.Manual, input.Reason!.Trim(), command.Actor,
                PaymentChangeCause.AllocationRevoked, ct);
            await db.SaveChangesAsync(ct);
            var outcome = new AllocationOutcome(allocation, await ViewAsync(transaction, ct), request);
            return new CommandResult(200, render(outcome),
                new { AllocationId = allocation.Id, TransactionId = transaction.Id, PaymentRequestId = request.Id });
        }, ct);
    }

    /// <summary>Excludes an unallocated, otherwise eligible transaction from matching, e.g. an unrelated receipt.</summary>
    public Task<ServiceResult<CommandOutcome>> IgnoreAsync(
        Guid callerTenantId, Guid? subtenantId, Guid transactionId, HandlingInput input, CommandRequest command,
        Func<TransactionView, object> render, CancellationToken ct = default) =>
        HandleAsync(callerTenantId, subtenantId, transactionId, input, command, render, async (transaction, reason) =>
        {
            if (MatchingRules.ManualBlock(transaction, await ledger.InOpenCaseAsync(transaction.Id, ct), true) is { } block)
            {
                return BlockError(block);
            }

            transaction.Handling = TransactionHandling.Ignored;
            transaction.HandlingReason = reason;
            transaction.HandledAt = Now();
            transaction.HandledBy = PaymentLedger.Label(command.Actor);
            transaction.AutoMatchBlocked = true;
            transaction.AutoMatchBlockReason = MatchResults.Ignored;
            tenants.AddAudit(command.Actor, transaction.TenantId, AuditActions.TransactionIgnored,
                new { TransactionId = transaction.Id, Reason = reason });
            return null;
        }, ct);

    /// <summary>Returns an ignored transaction to manual handling; automatic matching stays off until enabled.</summary>
    public Task<ServiceResult<CommandOutcome>> RestoreAsync(
        Guid callerTenantId, Guid? subtenantId, Guid transactionId, HandlingInput input, CommandRequest command,
        Func<TransactionView, object> render, CancellationToken ct = default) =>
        HandleAsync(callerTenantId, subtenantId, transactionId, input, command, render, (transaction, reason) =>
        {
            if (transaction.Handling != TransactionHandling.Ignored)
            {
                return Task.FromResult<ServiceError?>(ServiceError.Conflict("transaction_not_ignored",
                    "Only an ignored transaction can be restored; rejected and duplicate records are decided in their review case."));
            }

            transaction.Handling = TransactionHandling.Unallocated;
            transaction.HandlingReason = null;
            transaction.HandledAt = null;
            transaction.HandledBy = null;
            transaction.AutoMatchBlocked = true;
            transaction.AutoMatchBlockReason = RevocationCodes.Restored;
            tenants.AddAudit(command.Actor, transaction.TenantId, AuditActions.TransactionRestored,
                new { TransactionId = transaction.Id, Reason = reason });
            return Task.FromResult<ServiceError?>(null);
        }, ct);

    /// <summary>
    /// Removes a manual block and queues a fresh attempt. Cannot override a review, a duplicate or
    /// rejected record, an inactive owner, or a manual-only confirmation.
    /// </summary>
    public Task<ServiceResult<CommandOutcome>> EnableAutoMatchingAsync(
        Guid callerTenantId, Guid? subtenantId, Guid transactionId, HandlingInput input, CommandRequest command,
        Func<TransactionView, object> render, CancellationToken ct = default) =>
        HandleAsync(callerTenantId, subtenantId, transactionId, input, command, render, async (transaction, reason) =>
        {
            var wasBlocked = transaction.AutoMatchBlocked;
            transaction.AutoMatchBlocked = false;
            if (MatchingRules.AutomaticBlock(transaction, await ledger.InOpenCaseAsync(transaction.Id, ct), ledger.AllowTestEvidence) is { } block)
            {
                transaction.AutoMatchBlocked = wasBlocked;
                return ServiceError.Conflict("auto_matching_not_allowed", $"Automatic matching cannot be enabled: {block}.");
            }

            transaction.AutoMatchBlockReason = null;
            await jobs.QueueNextAsync(BackgroundJobTypes.MatchTransaction, transaction.Id, InboundFetchQueue.RequesterOf(command.Actor), ct);
            tenants.AddAudit(command.Actor, transaction.TenantId, AuditActions.TransactionAutoMatchingEnabled,
                new { TransactionId = transaction.Id, Reason = reason, WasBlocked = wasBlocked });
            return null;
        }, ct);

    public async Task<ServiceResult<IReadOnlyList<PaymentAllocation>>> ListAllocationsAsync(
        Guid callerTenantId, Guid? subtenantId, Guid requestId, CancellationToken ct = default)
    {
        if (await FindRequestAsync(callerTenantId, subtenantId, requestId, ct) is not { } request)
        {
            return ServiceError.NotFound();
        }

        return await db.PaymentAllocations.AsNoTracking()
            .Where(a => a.PaymentRequestId == request.Id && a.TenantId == request.TenantId)
            .OrderBy(a => a.CreatedAt).ThenBy(a => a.Id)
            .ToListAsync(ct);
    }

    /// <summary>Payment changes in sequence order, after <paramref name="afterSequence"/>.</summary>
    public async Task<ServiceResult<(IReadOnlyList<PaymentChange> Items, int? Next)>> ListChangesAsync(
        Guid callerTenantId, Guid? subtenantId, Guid requestId, int limit, int afterSequence, CancellationToken ct = default)
    {
        if (await FindRequestAsync(callerTenantId, subtenantId, requestId, ct) is not { } request)
        {
            return ServiceError.NotFound();
        }

        var items = await db.PaymentChanges.AsNoTracking()
            .Where(c => c.PaymentRequestId == request.Id && c.TenantId == request.TenantId && c.Sequence > afterSequence)
            .OrderBy(c => c.Sequence)
            .Take(limit + 1)
            .ToListAsync(ct);
        var hasMore = items.Count > limit;
        if (hasMore)
        {
            items.RemoveAt(items.Count - 1);
        }

        return (items, hasMore ? items[^1].Sequence : null);
    }

    /// <summary>Open review cases about transactions that are or were allocated to the request.</summary>
    public async Task<IReadOnlyList<Guid>> OpenReviewCasesOfRequestAsync(Guid requestId, CancellationToken ct) =>
        await db.ReviewCaseMembers.AsNoTracking()
            .Where(m => db.PaymentAllocations.Any(a => a.PaymentRequestId == requestId && a.TransactionId == m.TransactionId)
                && db.ReviewCases.Any(c => c.Id == m.CaseId && c.State == ReviewCaseState.Open))
            .Select(m => m.CaseId)
            .Distinct()
            .ToListAsync(ct);

    private async Task<ServiceResult<CommandOutcome>> HandleAsync(
        Guid callerTenantId, Guid? subtenantId, Guid transactionId, HandlingInput input, CommandRequest command,
        Func<TransactionView, object> render, Func<IncomingTransaction, string, Task<ServiceError?>> change, CancellationToken ct)
    {
        if (MatchingRules.ValidateReason(input.Reason) is { } reasonError)
        {
            return ServiceError.Validation("invalid_reason", "reason", reasonError);
        }

        if (await FindTransactionAsync(callerTenantId, subtenantId, transactionId, ct) is not ({ } owner, { } found))
        {
            return ServiceError.NotFound();
        }

        if (input.Version is not { } version)
        {
            return ServiceError.PreconditionRequired("Send expected_version: the transaction's version as shown to you.");
        }

        return await commands.RunAsync(owner.Id, found.BankAccountId, command, async () =>
        {
            var transaction = await db.IncomingTransactions.SingleAsync(t => t.Id == found.Id, ct);
            if (await ledger.LifecycleBlockAsync(owner.Id, transaction.BankConnectionId, ct) is { } lifecycle)
            {
                return LifecycleError(lifecycle);
            }

            if (transaction.Version != version)
            {
                return Stale();
            }

            if (await change(transaction, input.Reason!.Trim()) is { } error)
            {
                return error;
            }

            transaction.Version++;
            transaction.UpdatedAt = Now();
            await db.SaveChangesAsync(ct);
            return new CommandResult(200, render(await ViewAsync(transaction, ct)), new { TransactionId = transaction.Id });
        }, ct);
    }

    private static MatchingCandidate Candidate(IncomingTransaction transaction, PaymentRequest request, string? transactionBlock)
    {
        var reasons = new List<string>
        {
            transaction.VariableSymbolKey is null ? MatchResults.MissingVariableSymbol
            : transaction.VariableSymbolKey == request.VariableSymbolKey ? "variable_symbol_match"
            : "variable_symbol_mismatch",
            request.Amount == transaction.Amount ? "amount_match" : MatchResults.AmountMismatch,
        };

        if (request.Status == PaymentRequestStatus.Cancelled)
        {
            reasons.Add(MatchResults.RequestCancelled);
            return new MatchingCandidate(request, reasons, null, "payment_request_cancelled");
        }

        if (request.PaidAmount > 0)
        {
            reasons.Add(MatchResults.RequestAlreadyPaid);
        }

        var proposed = PaymentSummary.Add(request.PaidAmount, transaction.Amount) is { } paid
            ? PaymentSummary.Calculate(request.Amount, paid)
            : (PaymentSummary?)null;
        return new MatchingCandidate(request, reasons, proposed, transactionBlock ?? (proposed is null ? "amount_overflow" : null));
    }

    internal static ServiceError LifecycleError(string code) => code == MatchResults.BankConnectionArchived
        ? ServiceError.Conflict(code, "The bank connection is archived; its records are history only.")
        : ServiceError.Conflict(code, "The tenant or its parent is not active; reactivate it before changing payments.");

    private static ServiceError BlockError(string block) => block switch
    {
        MatchResults.AlreadyAllocated => ServiceError.Conflict("transaction_already_allocated", "The transaction already has an active allocation."),
        MatchResults.Ignored => ServiceError.Conflict("transaction_ignored", "The transaction is ignored; restore it first."),
        MatchResults.Rejected => ServiceError.Conflict("transaction_rejected", "A review rejected this transaction as a receipt."),
        MatchResults.Duplicate => ServiceError.Conflict("transaction_duplicate", "A review marked this transaction as a copy of another one."),
        MatchResults.NotLiveEvidence => ServiceError.Conflict("not_live_evidence", "Test and fixture transactions never pay requests."),
        MatchResults.AccountMovementConflict => ServiceError.Conflict(MatchResults.AccountMovementConflict,
            "An earlier registration of this bank account may have recorded this movement; an operator must confirm it before it can be allocated."),
        _ => ServiceError.Conflict("transaction_review_required",
            "The transaction needs review; resolve its review case before allocating it."),
    };

    internal static ServiceError Stale() =>
        ServiceError.PreconditionFailed("A record has changed since it was read; reload it and try again.");

    private Task<Tenant?> FindOwnerAsync(Guid callerTenantId, Guid? subtenantId, CancellationToken ct) =>
        subtenantId is { } childId
            ? tenants.FindSubtenantAsync(callerTenantId, childId, ct)
            : tenants.FindAsync(callerTenantId, ct);

    private async Task<(Tenant? Owner, IncomingTransaction? Transaction)> FindTransactionAsync(
        Guid callerTenantId, Guid? subtenantId, Guid transactionId, CancellationToken ct)
    {
        if (await FindOwnerAsync(callerTenantId, subtenantId, ct) is not { } owner)
        {
            return (null, null);
        }

        var transaction = await db.IncomingTransactions.AsNoTracking()
            .SingleOrDefaultAsync(t => t.Id == transactionId && t.TenantId == owner.Id, ct);
        return (owner, transaction);
    }

    private async Task<PaymentRequest?> FindRequestAsync(Guid callerTenantId, Guid? subtenantId, Guid requestId, CancellationToken ct) =>
        await FindOwnerAsync(callerTenantId, subtenantId, ct) is { } owner
            ? await db.PaymentRequests.AsNoTracking().SingleOrDefaultAsync(r => r.Id == requestId && r.TenantId == owner.Id, ct)
            : null;
}
