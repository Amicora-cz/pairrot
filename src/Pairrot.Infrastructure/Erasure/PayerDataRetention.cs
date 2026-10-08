using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pairrot.Core.Inbound;
using Pairrot.Infrastructure.Jobs;
using Pairrot.Infrastructure.Persistence;

// Raw SQL here is composed from constant fragments only; every value is a parameter.
#pragma warning disable EF1002, EF1003

namespace Pairrot.Infrastructure.Erasure;

/// <param name="MessagesByInstance">Messages whose content expired in this pass, per provider instance.</param>
public sealed record RetentionReport(
    DateTimeOffset Cutoff,
    IReadOnlyDictionary<string, int> MessagesByInstance,
    int Receipts,
    int ParseRuns,
    int ReviewCases,
    int JobErrors,
    int Transactions)
{
    public int Messages => MessagesByInstance.Values.Sum();
}

/// <summary>
/// Feature 014: on every tenant, what a notification said about the payer, and the stored email
/// itself, is purged <c>Retention:PayerData</c> after reception (default two years), whatever the
/// allocation or review state. Financial facts, symbols, statuses, and allocations stay; Feature
/// 008's shorter retention of foreign mail and confirmations is unaffected. Batches lock the
/// messages they purge, so a worker storing content or a parse result concurrently either commits
/// first (and is purged here) or sees the expired state and stores nothing.
/// </summary>
public class PayerDataRetention(PairrotDbContext db, BackgroundJobQueue jobs, IOptions<RetentionOptions> options, IOptions<ErasureOptions> erasure)
{
    public async Task<RetentionReport> RunAsync(CancellationToken ct = default)
    {
        var now = jobs.Now();
        var cutoff = options.Value.CutoffAt(now);
        var batch = Math.Max(1, erasure.Value.BatchSize);
        var byInstance = new Dictionary<string, int>(StringComparer.Ordinal);
        int receipts = 0, runs = 0, cases = 0, errors = 0, transactions = 0;

        while (true)
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var due = await db.Database.SqlQuery<DueMessage>($"""
                SELECT id, provider_instance_id FROM inbound_messages
                WHERE received_at < {cutoff} AND content_state NOT IN ('expired', 'erased')
                ORDER BY id LIMIT {batch}
                FOR UPDATE SKIP LOCKED
                """).ToListAsync(ct);
            if (due.Count == 0)
            {
                break;
            }

            var ids = due.Select(m => m.Id).ToArray();
            await PersonalDataPurge.MessagesAsync(db, ids, InboundContentState.Expired, now, ct);
            receipts += await PersonalDataPurge.ReceiptsAsync(db, ids, ct);
            runs += await PersonalDataPurge.ParseRunsOfMessagesAsync(db, ids, now, ct);
            cases += await PersonalDataPurge.ReviewCasesOfMessagesAsync(db, ids, PersonalDataPurge.ExpiredText, ct);
            errors += await PersonalDataPurge.JobErrorsAsync(db, ids, ct);
            await transaction.CommitAsync(ct);
            foreach (var group in due.GroupBy(m => m.ProviderInstanceId))
            {
                byInstance[group.Key] = byInstance.GetValueOrDefault(group.Key) + group.Count();
            }
        }

        while (true)
        {
            var purged = await db.Database.ExecuteSqlRawAsync(
                $"UPDATE incoming_transactions SET {PersonalDataPurge.PayerFieldsSql}, payer_data_purged_at = {{0}} "
                + "WHERE id IN (SELECT id FROM incoming_transactions WHERE received_at < {1} AND payer_data_purged_at IS NULL ORDER BY id LIMIT {2})",
                [now, cutoff, batch], ct);
            transactions += purged;
            if (purged < batch)
            {
                break;
            }
        }

        return new RetentionReport(cutoff, byInstance, receipts, runs, cases, errors, transactions);
    }

    private sealed class DueMessage
    {
        public Guid Id { get; set; }
        public string ProviderInstanceId { get; set; } = "";
    }
}

/// <summary>Runs <see cref="PayerDataRetention"/> at startup and then daily in the API process, unless <c>Retention:Enabled</c> is false.</summary>
public sealed class PayerDataRetentionScheduler(
    IServiceScopeFactory scopes, IOptions<RetentionOptions> options, ILogger<PayerDataRetentionScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (options.Value.Enabled)
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    var report = await scope.ServiceProvider.GetRequiredService<PayerDataRetention>().RunAsync(stoppingToken);
                    foreach (var (instance, count) in report.MessagesByInstance)
                    {
                        logger.LogInformation("Payer-data retention expired {Count} message(s) of {Instance}.", count, instance);
                    }

                    if (report.Transactions > 0)
                    {
                        logger.LogInformation("Payer-data retention purged the sender fields of {Count} transaction(s).", report.Transactions);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Payer-data retention failed.");
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromDays(1), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
