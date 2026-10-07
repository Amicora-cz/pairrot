using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pairrot.Core.Banking;
using Pairrot.Core.Inbound;
using Pairrot.Core.Jobs;
using Pairrot.Core.Tenants;
using Pairrot.Infrastructure.Jobs;
using Pairrot.Infrastructure.Persistence;

namespace Pairrot.Infrastructure.Transactions;

public sealed class MessageParsingOptions
{
    public const string Section = "MessageParsing";

    /// <summary>How often eligible messages without any parse job are looked for (they are also queued at startup).</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(10);
}

/// <summary>
/// Queues parsing of stored messages: with the content when it is stored, and by a sweep that
/// finds eligible messages that never had a parse job (messages stored before Feature 007, or
/// routed later by an operator). Whether a payment request exists plays no role.
/// </summary>
public class ParseScheduling(PairrotDbContext db, BackgroundJobQueue jobs)
{
    private const int SweepBatch = 200;

    /// <summary>
    /// Adds the first parse job of a message whose content is being stored in the caller's
    /// transaction, when nothing holds it. The worker checks eligibility again.
    /// </summary>
    public static void AddFirstJob(BackgroundJobQueue jobs, InboundMessage message)
    {
        if (message is { RoutingState: InboundRoutingState.Routed, HoldReason: null, ContentState: InboundContentState.Available })
        {
            jobs.Add(BackgroundJobTypes.ParseInboundMessage, message.Id, 1, BackgroundJobQueue.SystemRequester);
        }
    }

    /// <summary>Queues every eligible message that has never had a parse job; returns how many were queued.</summary>
    public async Task<int> QueueUnparsedAsync(CancellationToken ct = default)
    {
        var queued = 0;
        Guid? after = null;
        while (true)
        {
            var query = db.InboundMessages.AsNoTracking().Where(m =>
                m.RoutingState == InboundRoutingState.Routed
                && m.ContentState == InboundContentState.Available
                && m.HoldReason == null
                && !db.BackgroundJobs.Any(j => j.Type == BackgroundJobTypes.ParseInboundMessage && j.TargetId == m.Id)
                && db.BankConnections.Any(b => b.Id == m.BankConnectionId && b.Status == BankConnectionStatus.Active)
                && db.Tenants.Any(t => t.Id == m.TenantId && t.Status == TenantStatus.Active
                    && (t.ParentTenant == null || t.ParentTenant.Status == TenantStatus.Active)));
            if (after is { } cursor)
            {
                query = query.Where(m => m.Id.CompareTo(cursor) > 0);
            }

            var ids = await query.OrderBy(m => m.Id).Select(m => m.Id).Take(SweepBatch).ToListAsync(ct);
            foreach (var id in ids)
            {
                var (_, created) = await jobs.EnqueueAsync(BackgroundJobTypes.ParseInboundMessage, id, BackgroundJobQueue.SystemRequester, null, ct);
                queued += created ? 1 : 0;
            }

            if (ids.Count < SweepBatch)
            {
                return queued;
            }

            after = ids[^1];
        }
    }
}

/// <summary>Runs the parse sweep at startup and then periodically in the API process.</summary>
public sealed class MessageParseScheduler(
    IServiceScopeFactory scopes, IOptions<MessageParsingOptions> options, ILogger<MessageParseScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var queued = await scope.ServiceProvider.GetRequiredService<ParseScheduling>().QueueUnparsedAsync(stoppingToken);
                if (queued > 0)
                {
                    logger.LogInformation("Queued parsing of {Count} stored messages.", queued);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Parse sweep failed.");
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
