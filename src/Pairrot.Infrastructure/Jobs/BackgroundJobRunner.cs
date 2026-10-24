using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Pairrot.Infrastructure.Jobs;

/// <summary>Executes one job type. It must save its result through <see cref="BackgroundJobQueue.FinishAsync"/>.</summary>
public interface IBackgroundJobHandler
{
    string Type { get; }

    /// <summary>Whether this process can run the type at all; a process that cannot leaves the jobs to others.</summary>
    bool IsEnabled { get; }

    Task HandleAsync(JobLease lease, CancellationToken ct);
}

/// <summary>
/// Claims and runs due jobs of the types this process has enabled handlers for. A process that
/// cannot run a type (e.g. one with only the stub provider) leaves those jobs alone.
/// </summary>
public class BackgroundJobRunner(BackgroundJobQueue queue, IEnumerable<IBackgroundJobHandler> handlers)
{
    private static readonly string ProcessOwner = $"{Environment.MachineName}:{Environment.ProcessId}";

    private readonly Dictionary<string, IBackgroundJobHandler> _handlers = handlers.Where(h => h.IsEnabled).ToDictionary(h => h.Type);

    /// <summary>Runs one due job; <c>false</c> when none was due.</summary>
    public async Task<bool> RunOnceAsync(CancellationToken ct = default)
    {
        var lease = await ClaimAsync(ct);
        if (lease is null)
        {
            return false;
        }

        await _handlers[lease.Type].HandleAsync(lease, ct);
        return true;
    }

    /// <summary>Runs due jobs until none is left; returns how many ran.</summary>
    public async Task<int> RunDueAsync(CancellationToken ct = default)
    {
        var count = 0;
        while (await RunOnceAsync(ct))
        {
            count++;
        }

        return count;
    }

    public Task<JobLease?> ClaimAsync(CancellationToken ct = default) =>
        queue.ClaimNextAsync(_handlers.Keys, $"{ProcessOwner}:{Guid.NewGuid():N}", ct);

    public Task<DateTimeOffset?> NextDueAtAsync(CancellationToken ct = default) => queue.NextDueAtAsync(_handlers.Keys, ct);
}

internal static class WorkerWait
{
    /// <summary>Waits shorter than this are rounded up, so a due row that cannot be claimed yet does not spin the loop.</summary>
    public static readonly TimeSpan Minimum = TimeSpan.FromSeconds(1);

    /// <summary>After a failed round (e.g. the database is unreachable).</summary>
    public static readonly TimeSpan AfterError = TimeSpan.FromSeconds(30);

    /// <summary>The time until <paramref name="dueAt"/>, between <see cref="Minimum"/> and <paramref name="max"/>.</summary>
    public static TimeSpan Until(DateTimeOffset? dueAt, DateTimeOffset now, TimeSpan max)
    {
        if (dueAt is not { } due)
        {
            return max;
        }

        var wait = due - now;
        return wait < Minimum ? Minimum : wait > max ? max : wait;
    }
}

/// <summary>
/// Runs due jobs in the API process, then sleeps until the next due time, a saved job's signal, or
/// <see cref="BackgroundJobOptions.PollInterval"/>. Each round uses a fresh scope and database context.
/// </summary>
public sealed class BackgroundJobWorker(
    IServiceScopeFactory scopes, WorkSignals signals, TimeProvider time, IOptions<BackgroundJobOptions> options,
    ILogger<BackgroundJobWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan wait;
            try
            {
                bool ran;
                do
                {
                    using var scope = scopes.CreateScope();
                    ran = await scope.ServiceProvider.GetRequiredService<BackgroundJobRunner>().RunOnceAsync(stoppingToken);
                }
                while (ran && !stoppingToken.IsCancellationRequested);

                using (var scope = scopes.CreateScope())
                {
                    var next = await scope.ServiceProvider.GetRequiredService<BackgroundJobRunner>().NextDueAtAsync(stoppingToken);
                    wait = WorkerWait.Until(next, time.GetUtcNow(), options.Value.PollInterval);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // The job keeps its lease until it expires and is then resumed by the next round.
                logger.LogError(ex, "Background job round failed.");
                wait = WorkerWait.AfterError < options.Value.PollInterval ? WorkerWait.AfterError : options.Value.PollInterval;
            }

            try
            {
                await signals.Jobs.WaitAsync(wait, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
