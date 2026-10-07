using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pairrot.Core.Banking;
using Pairrot.Core.Jobs;
using Pairrot.Core.Matching;
using Pairrot.Core.Payments;
using Pairrot.Core.Tenants;
using Pairrot.Core.Transactions;
using Pairrot.Infrastructure.Jobs;
using Pairrot.Infrastructure.Persistence;
using Pairrot.Infrastructure.Tenants;
using Pairrot.Infrastructure.Transactions;

namespace Pairrot.Infrastructure.Matching;

/// <summary>
/// Automatic matching of one transaction, inside the caller's transaction and connection lock.
/// Rechecks every rule on fresh data; a candidate seen earlier is never a reservation.
/// </summary>
public class PaymentMatcher(PairrotDbContext db, PaymentLedger ledger)
{
    /// <summary>Records the result on the (tracked) transaction and allocates on an exact match; returns the result code.</summary>
    public async Task<string> TryMatchAsync(IncomingTransaction transaction, CancellationToken ct)
    {
        if (transaction.Handling != TransactionHandling.Unallocated)
        {
            // Already decided; the stored result stays as it was.
            return MatchingRules.ManualBlock(transaction, false, true)!;
        }

        var block = MatchingRules.AutomaticBlock(transaction, await ledger.InOpenCaseAsync(transaction.Id, ct), ledger.AllowTestEvidence)
            ?? await ledger.LifecycleBlockAsync(transaction.TenantId, transaction.BankConnectionId, ct);
        if (block is not null)
        {
            Record(transaction, block, null);
            return block;
        }

        var requests = transaction.VariableSymbolKey is { } key
            ? await db.PaymentRequests
                .Where(r => r.TenantId == transaction.TenantId && r.BankConnectionId == transaction.BankConnectionId && r.VariableSymbolKey == key)
                .ToListAsync(ct)
            : [];
        var result = MatchingRules.Decide(transaction, requests);
        var request = requests.Count == 1 ? requests[0] : null;
        Record(transaction, result, request?.Id);
        if (result == MatchResults.Matched)
        {
            if (ledger.Allocate(transaction, request!, AllocationMethod.Automatic, null, PaymentLedger.SystemActor).Error is { } error)
            {
                Record(transaction, error.Code, request!.Id);
                return error.Code;
            }
        }

        return result;
    }

    private void Record(IncomingTransaction transaction, string result, Guid? requestId)
    {
        transaction.MatchResult = result;
        transaction.MatchRequestId = requestId;
        transaction.MatchCheckedAt = ledger.Now();
    }
}

/// <summary>Matches one transaction; queued when it is recorded, on request, and by the sweep.</summary>
public class MatchTransactionJob(
    PairrotDbContext db, BackgroundJobQueue jobs, PaymentLedger ledger, PaymentMatcher matcher, ILogger<MatchTransactionJob> logger)
    : IBackgroundJobHandler
{
    public string Type => BackgroundJobTypes.MatchTransaction;

    public bool IsEnabled => true;

    public async Task HandleAsync(JobLease lease, CancellationToken ct)
    {
        var connectionId = await db.IncomingTransactions.AsNoTracking()
            .Where(t => t.Id == lease.TargetId).Select(t => (Guid?)t.BankConnectionId).SingleOrDefaultAsync(ct);
        await MatchingJobs.RunAsync(jobs, logger, lease, async job =>
        {
            if (connectionId is not { } connection)
            {
                jobs.Fail(job, "transaction_not_found", "The transaction does not exist.");
                return;
            }

            await ledger.LockConnectionAsync(connection, ct);
            var transaction = await db.IncomingTransactions.SingleAsync(t => t.Id == lease.TargetId, ct);
            await matcher.TryMatchAsync(transaction, ct);
            jobs.Succeed(job);
        }, ct);
    }
}

/// <summary>
/// A new request may be paid by a receipt that arrived before it: matches the waiting
/// transactions of its connection with the same symbol key, oldest first.
/// </summary>
public class MatchPaymentRequestJob(
    PairrotDbContext db, BackgroundJobQueue jobs, PaymentLedger ledger, PaymentMatcher matcher, ILogger<MatchPaymentRequestJob> logger)
    : IBackgroundJobHandler
{
    private const int MaxTransactions = 50;

    public string Type => BackgroundJobTypes.MatchPaymentRequest;

    public bool IsEnabled => true;

    public async Task HandleAsync(JobLease lease, CancellationToken ct)
    {
        var target = await db.PaymentRequests.AsNoTracking()
            .Where(r => r.Id == lease.TargetId)
            .Select(r => new { r.TenantId, r.BankConnectionId, r.VariableSymbolKey })
            .SingleOrDefaultAsync(ct);
        await MatchingJobs.RunAsync(jobs, logger, lease, async job =>
        {
            if (target is null)
            {
                jobs.Fail(job, "payment_request_not_found", "The payment request does not exist.");
                return;
            }

            await ledger.LockConnectionAsync(target.BankConnectionId, ct);
            var waiting = await db.IncomingTransactions
                .Where(t => t.TenantId == target.TenantId && t.BankConnectionId == target.BankConnectionId
                    && t.VariableSymbolKey == target.VariableSymbolKey && t.Handling == TransactionHandling.Unallocated
                    && !t.AutoMatchBlocked && !t.ManuallyConfirmed && t.IngestionStatus == IngestionStatus.Accepted)
                .OrderBy(t => t.Id)
                .Take(MaxTransactions)
                .ToListAsync(ct);
            foreach (var transaction in waiting)
            {
                await matcher.TryMatchAsync(transaction, ct);
            }

            jobs.Succeed(job);
        }, ct);
    }
}

internal static class MatchingJobs
{
    private const int SaveAttempts = 3;

    /// <summary>Finishes the job with its result; retries a lost race on fresh data, then the job's retry policy.</summary>
    public static async Task RunAsync(
        BackgroundJobQueue jobs, ILogger logger, JobLease lease, Func<BackgroundJob, Task> apply, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (!await jobs.FinishAsync(lease, apply, ct))
                {
                    logger.LogWarning("Job {JobId} lost its lease before finishing; its result was discarded.", lease.JobId);
                }

                return;
            }
            catch (DbUpdateException ex) when (attempt < SaveAttempts && IsRace(ex))
            {
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Matching job {JobId} failed.", lease.JobId);
                var next = jobs.Schedule.NextAttempt(lease.AttemptCount, jobs.Now());
                await jobs.FinishAsync(lease, job =>
                {
                    if (next is { } due)
                    {
                        BackgroundJobQueue.Retry(job, due, "matching_failed", $"Unexpected {ex.GetType().Name} while matching.");
                    }
                    else
                    {
                        jobs.Fail(job, "matching_failed", $"Unexpected {ex.GetType().Name} while matching. No attempts left.");
                    }

                    return Task.CompletedTask;
                }, ct);
                return;
            }
        }
    }

    private static bool IsRace(DbUpdateException ex) => ex is DbUpdateConcurrencyException
        || TenantService.IsUniqueViolation(ex, PaymentAllocationConfiguration.OneActiveIndex)
        || TenantService.IsUniqueViolation(ex, PaymentChangeConfiguration.SequenceUniqueIndex);
}

/// <summary>
/// Installs Feature 009 on existing data and keeps it complete: records review cases for
/// unresolved parser results that have none, and queues matching of eligible transactions that
/// never had a matching job. Both are idempotent and work in bounded batches.
/// </summary>
public class MatchingScheduling(PairrotDbContext db, BackgroundJobQueue jobs, PaymentLedger ledger, ReviewCaseRecorder recorder)
{
    private const int Batch = 200;

    public async Task<(int Cases, int Queued)> SweepAsync(CancellationToken ct = default)
    {
        var cases = await RecordMissingCasesAsync(ct);
        return (cases, await QueueUnmatchedAsync(ct));
    }

    /// <summary>
    /// Operator check of the invariants: each request's paid amount equals the sum of its active
    /// allocations, and every active allocation counts an allocated receipt that is in no open review
    /// case and not disputed, duplicate, or rejected. Returns one line per problem.
    /// </summary>
    public async Task<IReadOnlyList<string>> CheckConsistencyAsync(CancellationToken ct = default)
    {
        var totals = await db.PaymentRequests.AsNoTracking()
            .Select(r => new
            {
                r.Id,
                r.Status,
                r.Amount,
                r.PaidAmount,
                Allocated = db.PaymentAllocations.Where(a => a.PaymentRequestId == r.Id && a.State == AllocationState.Active)
                    .Sum(a => (long?)a.Amount) ?? 0,
            })
            .Where(r => r.PaidAmount != r.Allocated)
            .ToListAsync(ct);
        var problems = totals
            .Select(r => $"payment request {r.Id}: paid_amount {r.PaidAmount} but active allocations sum to {r.Allocated}")
            .ToList();

        var counted = await db.PaymentAllocations.AsNoTracking()
            .Where(a => a.State == AllocationState.Active)
            .Join(db.IncomingTransactions, a => a.TransactionId, t => t.Id, (a, t) => new { a.Id, Transaction = t })
            .Where(x => x.Transaction.Handling != TransactionHandling.Allocated
                || (x.Transaction.IngestionStatus != IngestionStatus.Accepted && !x.Transaction.ManuallyConfirmed)
                || db.ReviewCaseMembers.Any(m => m.TransactionId == x.Transaction.Id
                    && db.ReviewCases.Any(c => c.Id == m.CaseId && c.State == ReviewCaseState.Open)))
            .Select(x => new { x.Id, TransactionId = x.Transaction.Id })
            .ToListAsync(ct);
        problems.AddRange(counted.Select(x => $"allocation {x.Id}: counts transaction {x.TransactionId}, which is not eligible"));
        return problems;
    }

    /// <summary>Queues matching of every eligible transaction that has never had a matching job.</summary>
    public async Task<int> QueueUnmatchedAsync(CancellationToken ct = default)
    {
        var queued = 0;
        Guid? after = null;
        while (true)
        {
            var query = db.IncomingTransactions.AsNoTracking().Where(t =>
                t.Handling == TransactionHandling.Unallocated && !t.AutoMatchBlocked && !t.ManuallyConfirmed
                && t.IngestionStatus == IngestionStatus.Accepted
                && !db.BackgroundJobs.Any(j => j.Type == BackgroundJobTypes.MatchTransaction && j.TargetId == t.Id));
            if (after is { } cursor)
            {
                query = query.Where(t => t.Id.CompareTo(cursor) > 0);
            }

            var ids = await query.OrderBy(t => t.Id).Select(t => t.Id).Take(Batch).ToListAsync(ct);
            foreach (var id in ids)
            {
                var (_, created) = await jobs.EnqueueAsync(BackgroundJobTypes.MatchTransaction, id, BackgroundJobQueue.SystemRequester, null, ct);
                queued += created ? 1 : 0;
            }

            if (ids.Count < Batch)
            {
                return queued;
            }

            after = ids[^1];
        }
    }

    /// <summary>
    /// Review cases for transactions under review or disputed that belong to no case, and for
    /// messages whose latest run is an unaccepted candidate without a case (data from before
    /// Feature 009). Each connection is handled under its lock, so parsing cannot interleave.
    /// </summary>
    public async Task<int> RecordMissingCasesAsync(CancellationToken ct = default)
    {
        var recorded = 0;
        var connections = await db.IncomingTransactions.AsNoTracking()
            .Where(t => t.IngestionStatus != IngestionStatus.Accepted && !t.ManuallyConfirmed
                && t.Handling != TransactionHandling.Rejected && t.Handling != TransactionHandling.Duplicate
                && !db.ReviewCaseMembers.Any(m => m.TransactionId == t.Id))
            .Select(t => t.BankConnectionId)
            .Union(db.InboundMessages.AsNoTracking()
                .Where(m => m.LatestParseOutcome == Core.Parsing.ParseOutcome.ReviewRequired && m.BankConnectionId != null
                    && !db.ReviewCases.Any(c => c.InboundMessageId == m.Id)
                    && db.MessageParseRuns.Any(r => r.Id == m.LatestParseRunId && r.TransactionId == null && r.Candidate != null))
                .Select(m => m.BankConnectionId!.Value))
            .Distinct()
            .Take(Batch)
            .ToListAsync(ct);

        foreach (var connectionId in connections)
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await ledger.LockConnectionAsync(connectionId, ct);
            recorded += await RecordConnectionCasesAsync(connectionId, ct);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }

        db.ChangeTracker.Clear();
        return recorded;
    }

    private async Task<int> RecordConnectionCasesAsync(Guid connectionId, CancellationToken ct)
    {
        var recorded = 0;
        var uncovered = await db.IncomingTransactions
            .Where(t => t.BankConnectionId == connectionId && t.IngestionStatus != IngestionStatus.Accepted && !t.ManuallyConfirmed
                && t.Handling != TransactionHandling.Rejected && t.Handling != TransactionHandling.Duplicate
                && !db.ReviewCaseMembers.Any(m => m.TransactionId == t.Id))
            .OrderBy(t => t.Id)
            .ToListAsync(ct);

        foreach (var group in uncovered.Where(t => t.IngestionStatus == IngestionStatus.ReviewRequired).GroupBy(t => t.ComparisonFingerprint))
        {
            var first = group.First();
            var reviewCase = recorder.NewCase(first.TenantId, connectionId, ReviewIssue.PossibleDuplicate, null,
                Core.Parsing.ParseReasons.PossibleDuplicate,
                "Notifications with the same amount, date, and symbol may describe one payment or several.",
                ReviewCaseRecorder.Key(Core.Parsing.ParseReasons.PossibleDuplicate, first.ComparisonFingerprint));
            foreach (var t in group)
            {
                recorder.AddMember(reviewCase, t.Id, null);
                await ledger.LoseEligibilityAsync(t, ct);
            }

            recorded++;
        }

        foreach (var t in uncovered.Where(t => t.IngestionStatus == IngestionStatus.Disputed))
        {
            var reviewCase = recorder.NewCase(t.TenantId, connectionId, ReviewIssue.Disputed, null, "disputed",
                "Later evidence contradicts this transaction.", ReviewCaseRecorder.Key("disputed", t.Id.ToString()));
            recorder.AddMember(reviewCase, t.Id, null);
            await ledger.LoseEligibilityAsync(t, ct);
            recorded++;
        }

        var messages = await db.InboundMessages.AsNoTracking()
            .Where(m => m.BankConnectionId == connectionId && m.LatestParseOutcome == Core.Parsing.ParseOutcome.ReviewRequired
                && !db.ReviewCases.Any(c => c.InboundMessageId == m.Id))
            .Join(db.MessageParseRuns.Where(r => r.TransactionId == null && r.Candidate != null),
                m => m.LatestParseRunId, r => (Guid?)r.Id, (m, r) => new { m.Id, m.TenantId, Run = r })
            .ToListAsync(ct);
        foreach (var message in messages)
        {
            var candidate = System.Text.Json.JsonSerializer.Deserialize<Core.Parsing.TransferCandidate>(message.Run.Candidate!, MessageParseJob.Json)!;
            await recorder.OnUnverifiedCandidateAsync(message.Id, message.TenantId!.Value, connectionId, message.Run, candidate, ct);
            recorded++;
        }

        return recorded;
    }
}

/// <summary>Runs the matching sweep at startup and then periodically in the API process.</summary>
public sealed class MatchingScheduler(
    IServiceScopeFactory scopes, IOptions<MatchingOptions> options, ILogger<MatchingScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var (cases, queued) = await scope.ServiceProvider.GetRequiredService<MatchingScheduling>().SweepAsync(stoppingToken);
                if (cases + queued > 0)
                {
                    logger.LogInformation("Recorded {Cases} review cases and queued matching of {Queued} transactions.", cases, queued);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Matching sweep failed.");
            }

            try
            {
                await Task.Delay(options.Value.SweepInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
