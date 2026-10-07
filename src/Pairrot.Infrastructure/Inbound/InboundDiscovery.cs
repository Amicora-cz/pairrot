using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pairrot.Core.Inbound;
using Pairrot.Core.Jobs;
using Pairrot.Infrastructure.Jobs;
using Pairrot.Infrastructure.Persistence;
using Pairrot.Infrastructure.Tenants;

namespace Pairrot.Infrastructure.Inbound;

/// <summary>
/// Optional adapter capability: list an instance's received messages so deliveries whose
/// transport notification never arrived can be recovered. Pagination and vendor types stay in
/// the adapter; every listed message goes through common intake.
/// </summary>
public interface IInboundMessageDiscovery
{
    string Kind { get; }

    /// <summary>
    /// Submits every message the provider received at or after <paramref name="from"/> to
    /// <paramref name="sink"/>. The adapter may submit older messages too, but must not skip any in
    /// range, and must not assume date filtering or ordering the provider does not document.
    /// </summary>
    Task<DiscoveryResult> DiscoverAsync(InboundProviderInstance instance, DateTimeOffset from, IDiscoverySink sink, CancellationToken ct);
}

/// <summary>Receives discovered messages. A <c>false</c> answer stops the scan, which then counts as incomplete.</summary>
public interface IDiscoverySink
{
    Task<bool> AcceptAsync(InboundDelivery delivery, CancellationToken ct);

    /// <summary>Called after each provider page; renews the scan's lease.</summary>
    Task<bool> PageCompletedAsync(CancellationToken ct);
}

public enum DiscoveryOutcome
{
    /// <summary>Every message in range was submitted and recorded.</summary>
    Completed,

    /// <summary>The sink stopped the scan (lease lost or a message not saved).</summary>
    Aborted,

    TemporaryFailure,
    ConfigurationError,
    Invalid,
}

/// <param name="CoverageGap">History the provider cannot supply, e.g. beyond its retention.</param>
public sealed record DiscoveryResult(DiscoveryOutcome Outcome, string? Error = null, TimeSpan? RetryAfter = null, string? CoverageGap = null);

public sealed class InboundDiscoveryOptions
{
    public const string Section = "InboundEmail:Discovery";

    /// <summary>Time between scheduled checks (default 1 hour). One check is also scheduled at startup.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Each check also covers this much before the last completed boundary (default 1 hour).</summary>
    public TimeSpan Overlap { get; set; } = TimeSpan.FromHours(1);
}

/// <summary>Sync states and scheduling of recovery checks, for the scheduler and operators.</summary>
public class InboundDiscoveryService(
    PairrotDbContext db,
    BackgroundJobQueue jobs,
    InboundProviderRegistry providers,
    IEnumerable<IInboundMessageDiscovery> discoveries,
    IOptions<InboundDiscoveryOptions> options)
{
    /// <summary>Enabled instances whose adapter can list messages; others never get a check or an API call.</summary>
    public IReadOnlyList<InboundProviderInstance> Discoverable =>
        providers.All.Where(i => i.Enabled && discoveries.Any(d => d.Kind == i.Kind)).ToList();

    /// <summary>
    /// Queues a check for every discoverable instance that is due (all of them at startup). An
    /// instance with an active check gets no second one.
    /// </summary>
    public async Task<int> ScheduleAsync(bool startup, CancellationToken ct = default)
    {
        var queued = 0;
        foreach (var instance in Discoverable)
        {
            db.ChangeTracker.Clear();
            var state = await EnsureStateAsync(instance.Id, ct);
            var now = jobs.Now();
            if (!startup && state.NextDueAt is { } due && due > now)
            {
                continue;
            }

            try
            {
                var (_, created) = await jobs.EnqueueAsync(BackgroundJobTypes.DiscoverInboundMessages, state.Id, BackgroundJobQueue.SystemRequester,
                    async () =>
                    {
                        var tracked = await db.InboundSyncStates.SingleAsync(s => s.Id == state.Id, ct);
                        tracked.NextDueAt = now + options.Value.Interval;
                        tracked.UpdatedAt = now;
                    }, ct);
                queued += created ? 1 : 0;
            }
            catch (DbUpdateConcurrencyException)
            {
                // A check finished at the same moment; the next scheduling round looks again.
                db.ChangeTracker.Clear();
            }
        }

        return queued;
    }

    /// <summary>The instance's sync state, created on first activation with <c>sync_from</c> = now.</summary>
    public async Task<InboundSyncState> EnsureStateAsync(string instanceId, CancellationToken ct = default)
    {
        if (await db.InboundSyncStates.AsNoTracking().SingleOrDefaultAsync(s => s.ProviderInstanceId == instanceId, ct) is { } existing)
        {
            return existing;
        }

        var now = jobs.Now();
        var state = new InboundSyncState
        {
            Id = Guid.CreateVersion7(),
            ProviderInstanceId = instanceId,
            SyncFrom = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.InboundSyncStates.Add(state);
        try
        {
            await db.SaveChangesAsync(ct);
            return state;
        }
        catch (DbUpdateException ex) when (TenantService.IsUniqueViolation(ex, InboundSyncStateConfiguration.InstanceUniqueIndex))
        {
            db.ChangeTracker.Clear();
            return await db.InboundSyncStates.AsNoTracking().SingleAsync(s => s.ProviderInstanceId == instanceId, ct);
        }
    }

    public async Task<IReadOnlyList<(InboundSyncState State, BackgroundJob? LastJob)>> ListAsync(CancellationToken ct = default)
    {
        var states = await db.InboundSyncStates.AsNoTracking().OrderBy(s => s.ProviderInstanceId).ToListAsync(ct);
        var ids = states.Select(s => s.Id).ToList();
        var lastJobs = (await db.BackgroundJobs.AsNoTracking()
                .Where(j => j.Type == BackgroundJobTypes.DiscoverInboundMessages && ids.Contains(j.TargetId))
                .ToListAsync(ct))
            .GroupBy(j => j.TargetId)
            .ToDictionary(g => g.Key, g => g.MaxBy(j => j.Generation));
        return states.Select(s => (s, lastJobs.GetValueOrDefault(s.Id))).ToList();
    }

    /// <summary>Queues a check now (operator request), or returns the one already queued.</summary>
    public async Task<ServiceResult<QueuedRetrieval>> RunNowAsync(string instanceId, string requestedBy, CancellationToken ct = default)
    {
        if (Discoverable.All(i => i.Id != instanceId))
        {
            return ServiceError.Conflict("discovery_not_supported", "The instance is not enabled here or cannot list received messages.");
        }

        var state = await EnsureStateAsync(instanceId, ct);
        var (job, created) = await jobs.EnqueueAsync(BackgroundJobTypes.DiscoverInboundMessages, state.Id, requestedBy, null, ct);
        return new QueuedRetrieval(job, created);
    }

    /// <summary>
    /// Sets an earlier recovery start, e.g. when importing an existing receiving account. Moving it
    /// before the covered range restarts coverage there; a later value never hides history.
    /// </summary>
    public async Task<ServiceResult<InboundSyncState>> SetSyncFromAsync(string instanceId, DateTimeOffset from, CancellationToken ct = default)
    {
        if (providers.Find(instanceId) is null)
        {
            return ServiceError.NotFound("The instance is not configured.");
        }

        var state = await EnsureStateAsync(instanceId, ct);
        var tracked = await db.InboundSyncStates.SingleAsync(s => s.Id == state.Id, ct);
        if (from >= tracked.SyncFrom)
        {
            return ServiceError.Validation("sync_from can only move earlier; recorded coverage is never shrunk.");
        }

        tracked.SyncFrom = from.ToUniversalTime();
        tracked.CoveredThrough = null;
        tracked.CoverageGap = null;
        tracked.UpdatedAt = jobs.Now();
        await db.SaveChangesAsync(ct);
        return tracked;
    }
}

/// <summary>
/// Runs one recovery check. It captures an upper boundary at the start and advances the covered
/// boundary to it only after the whole range is recorded and only while it still holds the lease;
/// any incomplete scan leaves the boundary unchanged, so the next run repeats the same range.
/// </summary>
public class InboundDiscoveryJob(
    PairrotDbContext db,
    BackgroundJobQueue jobs,
    IInboundEmailIntake intake,
    InboundProviderRegistry providers,
    IEnumerable<IInboundMessageDiscovery> discoveries,
    IOptions<InboundDiscoveryOptions> options,
    ILogger<InboundDiscoveryJob> logger) : IBackgroundJobHandler
{
    public string Type => BackgroundJobTypes.DiscoverInboundMessages;
    public bool IsEnabled => providers.All.Any(i => i.Enabled && discoveries.Any(d => d.Kind == i.Kind));

    public async Task HandleAsync(JobLease lease, CancellationToken ct)
    {
        var state = await db.InboundSyncStates.AsNoTracking().SingleOrDefaultAsync(s => s.Id == lease.TargetId, ct);
        var instance = state is null ? null : providers.Find(state.ProviderInstanceId);
        var discovery = instance is { Enabled: true } ? discoveries.FirstOrDefault(d => d.Kind == instance.Kind) : null;
        if (state is null || discovery is null)
        {
            await FinishAsync(lease, state?.Id, (job, s) =>
            {
                jobs.Fail(job, "discovery_not_supported", "The instance is not enabled here or cannot list received messages.");
                RecordError(s, "discovery_not_supported", "The instance is not enabled here or cannot list received messages.");
            }, ct);
            return;
        }

        var upper = jobs.Now();
        var from = state.CoveredThrough is { } covered ? covered - options.Value.Overlap : state.SyncFrom;
        if (from < state.SyncFrom)
        {
            from = state.SyncFrom;
        }

        var sink = new Sink(intake, jobs, lease);
        var result = await discovery.DiscoverAsync(instance!, from, sink, ct);
        logger.LogInformation("Discovery of {Instance} from {From}: {Outcome}, {New} new message(s).",
            instance!.Id, from, result.Outcome, sink.NewMessages);

        switch (result.Outcome)
        {
            case DiscoveryOutcome.Completed:
                await FinishAsync(lease, state.Id, (job, s) =>
                {
                    s!.CoveredThrough = upper;
                    s.LastSuccessAt = jobs.Now();
                    s.DiscoveredCount += sink.NewMessages;
                    s.LastRunDiscoveredCount = sink.NewMessages;
                    s.LastErrorCode = null;
                    s.LastErrorMessage = null;
                    s.CoverageGap = result.CoverageGap;
                    jobs.Succeed(job);
                }, ct);
                break;
            case DiscoveryOutcome.TemporaryFailure or DiscoveryOutcome.Aborted when !sink.LeaseLost:
                var error = result.Outcome == DiscoveryOutcome.Aborted
                    ? sink.Error ?? "A discovered message could not be saved."
                    : result.Error ?? "The provider is temporarily unavailable.";
                var next = jobs.Schedule.NextAttempt(lease.AttemptCount, jobs.Now(), result.RetryAfter);
                await FinishAsync(lease, state.Id, (job, s) =>
                {
                    RecordError(s, "discovery_incomplete", error);
                    s!.DiscoveredCount += sink.NewMessages;
                    if (next is { } due)
                    {
                        BackgroundJobQueue.Retry(job, due, "discovery_incomplete", error);
                    }
                    else
                    {
                        jobs.Fail(job, "discovery_incomplete", $"{error} No attempts left; the next scheduled check repeats the range.");
                    }
                }, ct);
                break;
            case DiscoveryOutcome.Aborted:
                // The lease went to another worker; it owns the job now and nothing may be saved here.
                logger.LogWarning("Discovery job {JobId} lost its lease; the boundary was not advanced.", lease.JobId);
                break;
            default:
                var code = result.Outcome == DiscoveryOutcome.ConfigurationError ? "provider_unauthorized" : "invalid_provider_response";
                await FinishAsync(lease, state.Id, (job, s) =>
                {
                    RecordError(s, code, result.Error ?? "The provider could not list received messages.");
                    s!.DiscoveredCount += sink.NewMessages;
                    jobs.Fail(job, code, result.Error ?? "The provider could not list received messages.");
                }, ct);
                break;
        }
    }

    private void RecordError(InboundSyncState? state, string code, string error)
    {
        if (state is null)
        {
            return;
        }

        state.LastErrorCode = code;
        state.LastErrorMessage = BackgroundJobQueue.Truncate(error);
    }

    /// <summary>Saves the job and its sync state together, only while the lease is held. The attempt time is always recorded.</summary>
    private async Task FinishAsync(JobLease lease, Guid? stateId, Action<BackgroundJob, InboundSyncState?> apply, CancellationToken ct)
    {
        var saved = await jobs.FinishAsync(lease, async job =>
        {
            var state = stateId is { } id ? await db.InboundSyncStates.SingleOrDefaultAsync(s => s.Id == id, ct) : null;
            if (state is not null)
            {
                state.LastAttemptAt = jobs.Now();
                state.UpdatedAt = jobs.Now();
            }

            apply(job, state);
        }, ct);

        if (!saved)
        {
            logger.LogWarning("Discovery job {JobId} lost its lease before finishing; the boundary was not advanced.", lease.JobId);
        }
    }

    private sealed class Sink(IInboundEmailIntake intake, BackgroundJobQueue jobs, JobLease lease) : IDiscoverySink
    {
        public int NewMessages { get; private set; }
        public bool LeaseLost { get; private set; }
        public string? Error { get; private set; }

        public async Task<bool> AcceptAsync(InboundDelivery delivery, CancellationToken ct)
        {
            var result = await intake.AcceptAsync(delivery, ct);
            switch (result.Status)
            {
                case IntakeStatus.Accepted or IntakeStatus.Duplicate:
                    NewMessages += result.NewMessage ? 1 : 0;
                    return true;
                case IntakeStatus.Conflict:
                    // The discovery receipt exists with a different listing; the message itself is recorded.
                    return true;
                default:
                    Error = $"Message {delivery.SourceMessageId} could not be recorded ({result.Status}{(result.Error is null ? "" : $": {result.Error}")}).";
                    return false;
            }
        }

        public async Task<bool> PageCompletedAsync(CancellationToken ct)
        {
            LeaseLost = !await jobs.RenewAsync(lease, ct);
            return !LeaseLost;
        }
    }
}

/// <summary>Schedules one recovery check when the application starts, then checks at the configured interval.</summary>
public sealed class InboundDiscoveryScheduler(
    IServiceScopeFactory scopes, IOptions<InboundDiscoveryOptions> options, ILogger<InboundDiscoveryScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan DatabaseRetry = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Runs after startup completes, so HTTP is available while the database is still being reached.
        await Task.Yield();
        var startup = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan wait;
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<InboundDiscoveryService>().ScheduleAsync(startup, stoppingToken);
                startup = false;
                wait = options.Value.Interval < TimeSpan.FromMinutes(1) ? options.Value.Interval : TimeSpan.FromMinutes(1);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not schedule inbound discovery; retrying.");
                wait = DatabaseRetry;
            }

            try
            {
                await Task.Delay(wait, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
