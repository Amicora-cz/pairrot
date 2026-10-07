using Microsoft.EntityFrameworkCore;
using Pairrot.Core.Webhooks;
using Pairrot.Infrastructure.Jobs;
using Pairrot.Infrastructure.Persistence;

namespace Pairrot.Infrastructure.Webhooks;

/// <summary>
/// Periodic upkeep: retires signing keys whose grace period ended (removing their secret),
/// releases holds whose cause is gone, and materializes events for payment changes recorded
/// before Feature 010 (without deliveries: old history is sent only on explicit replay).
/// </summary>
public class WebhookMaintenance(PairrotDbContext db, BackgroundJobQueue jobs)
{
    private const int Batch = 200;

    public async Task RunAsync(CancellationToken ct = default)
    {
        await RetireExpiredKeysAsync(ct);
        await ReleaseResolvedHoldsAsync(ct);
        await MaterializeHistoryAsync(ct);
    }

    public async Task<int> RetireExpiredKeysAsync(CancellationToken ct = default)
    {
        var now = jobs.Now();
        return await db.WebhookSigningKeys
            .Where(k => k.State == WebhookKeyState.Retiring && k.RetiresAt <= now)
            .ExecuteUpdateAsync(u => u
                .SetProperty(k => k.State, WebhookKeyState.Retired)
                .SetProperty(k => k.ProtectedSecret, (string?)null)
                .SetProperty(k => k.RetiredAt, now), ct);
    }

    /// <summary>Held for an inactive tenant, missing authority, or an unavailable key: back to the queue once current checks pass.</summary>
    public async Task<int> ReleaseResolvedHoldsAsync(CancellationToken ct = default)
    {
        var soft = WebhookDeliveryWorker.SoftHolds.ToList();
        var held = await db.WebhookDeliveries
            .Where(d => d.State == WebhookDeliveryState.Held && soft.Contains(d.HoldReason!))
            .OrderBy(d => d.Id)
            .Take(Batch)
            .ToListAsync(ct);
        var released = 0;
        foreach (var delivery in held)
        {
            var endpoint = await db.WebhookEndpoints.AsNoTracking().SingleAsync(e => e.Id == delivery.EndpointId, ct);
            var ev = await db.WebhookEvents.AsNoTracking().SingleAsync(e => e.Id == delivery.EventId, ct);
            var reason = await WebhookDeliveryWorker.HoldReasonAsync(db, delivery, endpoint, ev, ct);
            var keyAvailable = delivery.HoldReason != WebhookHolds.SigningKeyUnavailable
                || await db.WebhookSigningKeys.AnyAsync(k => k.EndpointId == endpoint.Id && k.State == WebhookKeyState.Current, ct);
            if (reason is null && keyAvailable)
            {
                delivery.State = WebhookDeliveryState.Pending;
                delivery.HoldReason = null;
                delivery.DueAt = jobs.Now();
                delivery.UpdatedAt = jobs.Now();
                delivery.Version++;
                released++;
            }
        }

        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        return released;
    }

    /// <summary>Events for existing Feature 009 changes, keeping their IDs and sequences.</summary>
    public async Task<int> MaterializeHistoryAsync(CancellationToken ct = default)
    {
        var created = 0;
        while (true)
        {
            var changes = await db.PaymentChanges.AsNoTracking()
                .Where(c => !db.WebhookEvents.Any(e => e.PaymentChangeId == c.Id))
                .OrderBy(c => c.Id)
                .Take(Batch)
                .ToListAsync(ct);
            foreach (var change in changes)
            {
                var body = WebhookEvents.PaymentBody(change);
                db.WebhookEvents.Add(new WebhookEvent
                {
                    Id = change.Id,
                    Type = WebhookEvents.PaymentRequestUpdated,
                    TenantId = change.TenantId,
                    PaymentChangeId = change.Id,
                    PaymentRequestId = change.PaymentRequestId,
                    Sequence = change.Sequence,
                    Environment = WebhookEvents.EnvironmentOf(change),
                    Body = body,
                    BodySha256 = WebhookEvents.Sha256(body),
                    CreatedAt = change.CreatedAt,
                });
            }

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (Tenants.TenantService.IsUniqueViolation(ex, WebhookEventConfiguration.ChangeUniqueIndex)
                || ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
            {
                // A concurrent payment change routed its own event first; the next round skips it.
            }

            db.ChangeTracker.Clear();
            created += changes.Count;
            if (changes.Count < Batch)
            {
                return created;
            }
        }
    }
}
