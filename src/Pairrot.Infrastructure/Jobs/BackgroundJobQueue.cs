using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pairrot.Core.Jobs;
using Pairrot.Infrastructure.Persistence;
using Pairrot.Infrastructure.Tenants;

namespace Pairrot.Infrastructure.Jobs;

public sealed class BackgroundJobOptions
{
    public const string Section = "BackgroundJobs";

    private static readonly TimeSpan[] DefaultDelays =
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2), TimeSpan.FromHours(6)];

    /// <summary>Whether this process runs the worker. Tests turn it off and run jobs explicitly.</summary>
    public bool Enabled { get; set; } = true;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a claimed job is reserved; after that, another worker may resume it.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(5);

    public int MaxAttempts { get; set; } = 6;

    /// <summary>Waits after attempt 1, 2, ... (default 1 min, 5 min, 30 min, 2 h, 6 h).</summary>
    public TimeSpan[]? RetryDelays { get; set; }

    public RetrySchedule Schedule => new(MaxAttempts, RetryDelays is { Length: > 0 } delays ? delays : DefaultDelays);
}

/// <summary>A claimed job. Results may only be saved through <see cref="BackgroundJobQueue.FinishAsync"/> with this lease.</summary>
public sealed record JobLease(Guid JobId, string Type, Guid TargetId, int Generation, int AttemptCount, Guid Token);

/// <summary>
/// The PostgreSQL job table: claiming with <c>FOR UPDATE SKIP LOCKED</c> and an expiring lease,
/// and finishing only while the lease is still held, together with the job's result.
/// </summary>
public class BackgroundJobQueue(PairrotDbContext db, TimeProvider time, IOptions<BackgroundJobOptions> options)
{
    public const string SystemRequester = "system";

    public RetrySchedule Schedule => options.Value.Schedule;

    public DateTimeOffset Now()
    {
        var now = time.GetUtcNow();
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMicrosecond));
    }

    /// <summary>Adds a due job to the context; the caller saves it in the same transaction as the work that needs it.</summary>
    public BackgroundJob Add(string type, Guid targetId, int generation, string requestedBy)
    {
        var now = Now();
        var job = new BackgroundJob
        {
            Id = Guid.CreateVersion7(),
            Type = type,
            TargetId = targetId,
            Generation = generation,
            State = BackgroundJobState.Queued,
            DueAt = now,
            RequestedBy = requestedBy,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.BackgroundJobs.Add(job);
        return job;
    }

    /// <summary>
    /// Queues the next generation of an operation, or returns the job that is still active for it.
    /// <paramref name="addRelatedChanges"/> adds changes (e.g. an audit event) saved only with a new job.
    /// </summary>
    public async Task<(BackgroundJob Job, bool Created)> EnqueueAsync(
        string type, Guid targetId, string requestedBy, Func<Task>? addRelatedChanges, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var jobs = await db.BackgroundJobs.AsNoTracking()
                .Where(j => j.Type == type && j.TargetId == targetId)
                .OrderByDescending(j => j.Generation)
                .ToListAsync(ct);
            if (jobs.FirstOrDefault(IsActive) is { } active)
            {
                return (active, false);
            }

            var job = Add(type, targetId, (jobs.FirstOrDefault()?.Generation ?? 0) + 1, requestedBy);
            if (addRelatedChanges is not null)
            {
                await addRelatedChanges();
            }

            try
            {
                await db.SaveChangesAsync(ct);
                return (job, true);
            }
            catch (DbUpdateException ex) when (attempt < 3
                && (TenantService.IsUniqueViolation(ex, BackgroundJobConfiguration.OneActiveIndex)
                    || TenantService.IsUniqueViolation(ex, BackgroundJobConfiguration.GenerationUniqueIndex)))
            {
                // A concurrent request queued it first; return that job.
                db.ChangeTracker.Clear();
            }
        }
    }

    public static bool IsActive(BackgroundJob job) =>
        job.State is BackgroundJobState.Queued or BackgroundJobState.Running or BackgroundJobState.RetryWait;

    /// <summary>
    /// Claims the next due job of the given types, including a running job whose lease expired
    /// (its worker stopped). Concurrent workers never claim the same job.
    /// </summary>
    public async Task<JobLease?> ClaimNextAsync(IReadOnlyCollection<string> types, string owner, CancellationToken ct)
    {
        if (types.Count == 0)
        {
            return null;
        }

        var now = Now();
        var token = Guid.NewGuid();
        var expires = now + options.Value.LeaseDuration;
        var typeArray = types.ToArray();

        var claimed = await db.BackgroundJobs.FromSql($"""
            UPDATE background_jobs
            SET state = 'running', attempt_count = attempt_count + 1, lease_owner = {owner}, lease_token = {token},
                lease_expires_at = {expires}, updated_at = {now}
            WHERE id = (
                SELECT id FROM background_jobs
                WHERE type = ANY({typeArray})
                  AND ((state IN ('queued', 'retry_wait') AND due_at <= {now}) OR (state = 'running' AND lease_expires_at <= {now}))
                ORDER BY due_at, id
                LIMIT 1
                FOR UPDATE SKIP LOCKED)
            RETURNING *
            """).AsNoTracking().ToListAsync(ct);

        return claimed is [var job]
            ? new JobLease(job.Id, job.Type, job.TargetId, job.Generation, job.AttemptCount, token)
            : null;
    }

    /// <summary>
    /// Extends a long-running job's lease. <c>false</c> when another worker has taken the job over:
    /// the caller must stop, and can no longer save anything.
    /// </summary>
    public async Task<bool> RenewAsync(JobLease lease, CancellationToken ct)
    {
        var now = Now();
        var expires = now + options.Value.LeaseDuration;
        var renewed = await db.Database.ExecuteSqlAsync($"""
            UPDATE background_jobs SET lease_expires_at = {expires}, updated_at = {now}
            WHERE id = {lease.JobId} AND state = 'running' AND lease_token = {lease.Token}
            """, ct);
        return renewed == 1;
    }

    /// <summary>
    /// Saves a job's result in one transaction with its completion, but only if <paramref name="lease"/>
    /// still holds the job. <paramref name="apply"/> changes tracked entities and must set the job's
    /// next state with <see cref="Succeed"/>, <see cref="Fail"/>, or <see cref="Retry"/>.
    /// Returns <c>false</c>, saving nothing, when the lease was lost to another worker.
    /// </summary>
    public async Task<bool> FinishAsync(JobLease lease, Func<BackgroundJob, Task> apply, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var rows = await db.BackgroundJobs
            .FromSql($"SELECT * FROM background_jobs WHERE id = {lease.JobId} FOR UPDATE")
            .ToListAsync(ct);
        if (rows is not [{ State: BackgroundJobState.Running } job] || job.LeaseToken != lease.Token)
        {
            return false;
        }

        await apply(job);
        if (job.State == BackgroundJobState.Running)
        {
            throw new InvalidOperationException("A finished job must leave the running state.");
        }

        job.LeaseOwner = null;
        job.LeaseToken = null;
        job.LeaseExpiresAt = null;
        job.UpdatedAt = Now();
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    public void Succeed(BackgroundJob job)
    {
        job.State = BackgroundJobState.Succeeded;
        job.CompletedAt = Now();
        job.LastErrorCode = null;
        job.LastErrorMessage = null;
    }

    public void Fail(BackgroundJob job, string code, string message)
    {
        job.State = BackgroundJobState.Failed;
        job.CompletedAt = Now();
        job.LastErrorCode = code;
        job.LastErrorMessage = Truncate(message);
    }

    public static void Retry(BackgroundJob job, DateTimeOffset dueAt, string code, string message)
    {
        job.State = BackgroundJobState.RetryWait;
        job.DueAt = dueAt;
        job.LastErrorCode = code;
        job.LastErrorMessage = Truncate(message);
    }

    internal static string Truncate(string message) => message.Length <= 500 ? message : message[..500];
}
