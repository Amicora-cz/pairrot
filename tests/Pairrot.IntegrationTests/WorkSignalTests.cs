using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pairrot.Core.Jobs;
using Pairrot.Infrastructure.Jobs;
using Pairrot.Infrastructure.Persistence;

namespace Pairrot.IntegrationTests;

/// <summary>
/// Workers sleep until the next due time and are woken by saved work, so an idle API does not
/// query the database. Uses its own signals and a job type no worker runs, so the host's worker
/// neither consumes the notifications nor claims the jobs.
/// </summary>
[Collection(PairrotCollection.Name)]
public class WorkSignalTests(PairrotFixture fx)
{
    private const string Type = "test_work_signal";
    private static readonly TimeSpan NoSignal = TimeSpan.FromMilliseconds(200);

    private PairrotDbContext NewContext(WorkSignals signals) => new(new DbContextOptionsBuilder<PairrotDbContext>()
        .UseNpgsql(fx.ConnectionString).UseSnakeCaseNamingConvention()
        .AddInterceptors(new WorkSignalInterceptor(signals)).Options);

    private static BackgroundJobQueue Queue(PairrotDbContext db) =>
        new(db, TimeProvider.System, Options.Create(new BackgroundJobOptions()));

    [Fact]
    public async Task A_saved_job_wakes_the_job_worker_only()
    {
        var signals = new WorkSignals();
        await using var db = NewContext(signals);
        Queue(db).Add(Type, Guid.NewGuid(), 1, BackgroundJobQueue.SystemRequester);
        await db.SaveChangesAsync();

        Assert.True(await signals.Jobs.WaitAsync(TimeSpan.Zero, CancellationToken.None));
        Assert.False(await signals.Webhooks.WaitAsync(TimeSpan.Zero, CancellationToken.None));
    }

    [Fact]
    public async Task A_job_saved_in_a_transaction_wakes_the_worker_at_commit_and_not_after_rollback()
    {
        var signals = new WorkSignals();
        await using var db = NewContext(signals);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            Queue(db).Add(Type, Guid.NewGuid(), 1, BackgroundJobQueue.SystemRequester);
            await db.SaveChangesAsync();
            Assert.False(await signals.Jobs.WaitAsync(NoSignal, CancellationToken.None));
            await transaction.CommitAsync();
        }

        Assert.True(await signals.Jobs.WaitAsync(TimeSpan.Zero, CancellationToken.None));

        db.ChangeTracker.Clear();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            Queue(db).Add(Type, Guid.NewGuid(), 1, BackgroundJobQueue.SystemRequester);
            await db.SaveChangesAsync();
            await transaction.RollbackAsync();
        }

        Assert.False(await signals.Jobs.WaitAsync(NoSignal, CancellationToken.None));
    }

    [Fact]
    public async Task The_next_due_time_is_the_earliest_due_job_or_lease_expiry_of_the_given_types()
    {
        var type = $"{Type}_{Guid.NewGuid():N}"[..40];
        await using var db = NewContext(new WorkSignals());
        var queue = Queue(db);
        Assert.Null(await queue.NextDueAtAsync([type], CancellationToken.None));

        var later = queue.Add(type, Guid.NewGuid(), 1, BackgroundJobQueue.SystemRequester);
        later.DueAt = queue.Now().AddHours(2);
        var sooner = queue.Add(type, Guid.NewGuid(), 1, BackgroundJobQueue.SystemRequester);
        sooner.State = BackgroundJobState.RetryWait;
        sooner.DueAt = queue.Now().AddMinutes(30);
        var done = queue.Add(type, Guid.NewGuid(), 1, BackgroundJobQueue.SystemRequester);
        done.DueAt = queue.Now().AddMinutes(-5);
        queue.Succeed(done);
        await db.SaveChangesAsync();
        Assert.Equal(sooner.DueAt, await queue.NextDueAtAsync([type], CancellationToken.None));

        var lease = await queue.ClaimNextAsync([type], "worker", CancellationToken.None);
        Assert.Null(lease);
        later.DueAt = queue.Now().AddMinutes(-1);
        await db.SaveChangesAsync();
        lease = (await queue.ClaimNextAsync([type], "worker", CancellationToken.None))!;
        var expires = await db.BackgroundJobs.AsNoTracking().Where(j => j.Id == lease.JobId).Select(j => j.LeaseExpiresAt).SingleAsync();
        Assert.Equal(expires, await queue.NextDueAtAsync([type], CancellationToken.None));
    }
}
