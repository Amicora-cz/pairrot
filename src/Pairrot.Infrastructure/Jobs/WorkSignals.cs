using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Pairrot.Core.Jobs;
using Pairrot.Core.Webhooks;
using Pairrot.Infrastructure.Persistence;

namespace Pairrot.Infrastructure.Jobs;

/// <summary>
/// Wakes a worker as soon as work for it is committed in this process, so it can sleep until the
/// next known due time instead of polling the database (which also lets a serverless database idle).
/// </summary>
public sealed class WorkSignal
{
    private readonly SemaphoreSlim _semaphore = new(0, 1);

    public void Notify()
    {
        try
        {
            _semaphore.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signalled; the worker has not woken yet.
        }
    }

    /// <summary>
    /// Returns <c>true</c> when notified (also by a notification that came while the worker was busy),
    /// <c>false</c> after <paramref name="timeout"/>.
    /// </summary>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => _semaphore.WaitAsync(timeout, ct);
}

public sealed class WorkSignals
{
    public WorkSignal Jobs { get; } = new();

    public WorkSignal Webhooks { get; } = new();
}

/// <summary>
/// Notifies the workers after a save that adds or changes background jobs or webhook deliveries,
/// or changes an endpoint (an activated endpoint makes its deliveries due). A save inside an
/// explicit transaction notifies when the transaction commits, so the worker sees the rows.
/// </summary>
public sealed class WorkSignalInterceptor(WorkSignals signals) : SaveChangesInterceptor, IDbTransactionInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Collect(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Collect(eventData.Context);
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Saved(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Saved(eventData.Context);
        return result;
    }

    public void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) => Flush(eventData.Context);

    public Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Flush(eventData.Context);
        return Task.CompletedTask;
    }

    public void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) => Discard(eventData.Context);

    public Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Discard(eventData.Context);
        return Task.CompletedTask;
    }

    private static void Collect(DbContext? context)
    {
        if (context is not PairrotDbContext db)
        {
            return;
        }

        foreach (var entry in db.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified))
            {
                continue;
            }

            switch (entry.Entity)
            {
                case BackgroundJob:
                    db.SavingWork |= PendingWork.Jobs;
                    break;
                case WebhookDelivery or WebhookEndpoint:
                    db.SavingWork |= PendingWork.Webhooks;
                    break;
            }
        }
    }

    private void Saved(DbContext? context)
    {
        if (context is not PairrotDbContext db)
        {
            return;
        }

        db.PendingWork |= db.SavingWork;
        db.SavingWork = PendingWork.None;
        if (db.Database.CurrentTransaction is null)
        {
            Flush(db);
        }
    }

    private void Flush(DbContext? context)
    {
        if (context is not PairrotDbContext db)
        {
            return;
        }

        var work = db.PendingWork;
        db.PendingWork = PendingWork.None;
        if (work.HasFlag(PendingWork.Jobs))
        {
            signals.Jobs.Notify();
        }

        if (work.HasFlag(PendingWork.Webhooks))
        {
            signals.Webhooks.Notify();
        }
    }

    private static void Discard(DbContext? context)
    {
        if (context is PairrotDbContext db)
        {
            db.PendingWork = PendingWork.None;
        }
    }
}

[Flags]
internal enum PendingWork
{
    None = 0,
    Jobs = 1,
    Webhooks = 2,
}
