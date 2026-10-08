using Microsoft.EntityFrameworkCore;
using Pairrot.Infrastructure.Persistence;

namespace Pairrot.Infrastructure.Erasure;

public sealed class ErasureOptions
{
    public const string Section = "Erasure";

    /// <summary>Wait after deletion before an erasure is accepted over HTTP; zero disables it.</summary>
    public TimeSpan MinimumAgeAfterDeletion { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Records per erasure batch; each batch is its own transaction.</summary>
    public int BatchSize { get; set; } = 500;
}

public sealed class RetentionOptions
{
    public const string Section = "Retention";

    /// <summary>Age, counted from reception, after which payer data is purged on every tenant.</summary>
    public TimeSpan PayerData { get; set; } = TimeSpan.FromDays(730);

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Messages received before this time hold no payer data: the cutoff of a retention pass, and
    /// what intake and workers check before they store content.
    /// </summary>
    public DateTimeOffset CutoffAt(DateTimeOffset now) => now - PayerData;
}

/// <summary>
/// Feature 014's collection barrier. A data-producing commit (intake, content retrieval, parsing,
/// webhook events and results) takes the owner's shared barrier lock in its own transaction and
/// then checks <c>tenants.erasure_requested_at</c>; requesting erasure sets it under the
/// exclusive lock. So a commit either finished before the request (and erasure purges it) or
/// sees the barrier and stores only minimized tombstones. The barrier is permanent, so the
/// timestamp itself serves as the tenant's data epoch.
/// </summary>
public static class DataBarrier
{
    /// <summary>Takes the shared lock (until the transaction ends) and reports whether the tenant's barrier is set.</summary>
    public static async Task<bool> IsClosedAsync(PairrotDbContext db, Guid tenantId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock_shared(hashtextextended({tenantId.ToString()}, 1014))", ct);
        return await db.Tenants.AsNoTracking().AnyAsync(t => t.Id == tenantId && t.ErasureRequestedAt != null, ct);
    }

    /// <summary>Waits until no data-producing commit of the tenant is in flight; held until the transaction ends.</summary>
    public static Task LockExclusiveAsync(PairrotDbContext db, Guid tenantId, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({tenantId.ToString()}, 1014))", ct);
}
