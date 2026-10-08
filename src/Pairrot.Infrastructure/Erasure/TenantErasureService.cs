using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pairrot.Core.Jobs;
using Pairrot.Core.Matching;
using Pairrot.Core.Tenants;
using Pairrot.Infrastructure.Jobs;
using Pairrot.Infrastructure.Matching;
using Pairrot.Infrastructure.Persistence;
using Pairrot.Infrastructure.Tenants;

namespace Pairrot.Infrastructure.Erasure;

/// <param name="Replayed">The tenant's erasure was already requested; nothing changed now.</param>
public sealed record ErasureRequestOutcome(TenantErasure Erasure, bool Replayed);

/// <summary>An erasure with its tenant, for the CLI status listing.</summary>
public sealed record ErasureStatus(Tenant Tenant, TenantErasure Erasure);

/// <summary>
/// Feature 014: requests the erasure of a deleted tenant. The request sets the tenant's permanent
/// collection barrier under the exclusive barrier lock, abandons queued webhook deliveries of the
/// tenant's events at every destination, disables the tenant's own endpoint and drops its signing
/// secrets, and queues <see cref="TenantErasureJob"/>, all in one transaction. Repeating it
/// returns the existing erasure, whatever its key.
/// </summary>
public class TenantErasureService(
    PairrotDbContext db, TenantService tenants, BackgroundJobQueue jobs, IOptions<ErasureOptions> options)
{
    /// <summary>A parent (management key or administrator session) erases a deleted direct child.</summary>
    public async Task<ServiceResult<ErasureRequestOutcome>> RequestForSubtenantAsync(
        Guid ownerId, Guid childId, string? reason, string idempotencyKey, Actor actor, CancellationToken ct = default)
    {
        if (await tenants.FindSubtenantAsync(ownerId, childId, ct) is null)
        {
            return ServiceError.NotFound();
        }

        return await RequestAsync(childId, reason, idempotencyKey, actor, asOperator: false, ct);
    }

    /// <summary>
    /// The operator erases any tenant, a root included. A tenant that is not deleted yet is deleted
    /// first, in the same transaction; there is no minimum age. A tenant with children that are not
    /// deleted cannot be erased.
    /// </summary>
    public Task<ServiceResult<ErasureRequestOutcome>> RequestAsOperatorAsync(
        Guid tenantId, string? reason, Actor actor, CancellationToken ct = default) =>
        RequestAsync(tenantId, reason, null, actor, asOperator: true, ct);

    public Task<TenantErasure?> FindAsync(Guid tenantId, CancellationToken ct = default) =>
        db.TenantErasures.AsNoTracking().SingleOrDefaultAsync(e => e.TenantId == tenantId, ct);

    /// <summary>Erasures, newest first, optionally of one tenant.</summary>
    public async Task<IReadOnlyList<ErasureStatus>> ListAsync(Guid? tenantId, CancellationToken ct = default) =>
        await (from e in db.TenantErasures.AsNoTracking()
               join t in db.Tenants.AsNoTracking().Include(t => t.ParentTenant) on e.TenantId equals t.Id
               where tenantId == null || e.TenantId == tenantId
               orderby e.RequestedAt descending
               select new ErasureStatus(t, e)).ToListAsync(ct);

    private async Task<ServiceResult<ErasureRequestOutcome>> RequestAsync(
        Guid tenantId, string? reason, string? idempotencyKey, Actor actor, bool asOperator, CancellationToken ct)
    {
        if (MatchingRules.ValidateReason(reason) is { } reasonError)
        {
            return ServiceError.Validation("invalid_reason", "reason", reasonError);
        }

        for (var attempt = 1; ; attempt++)
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            // Waits for data-producing commits already past the barrier check; later ones see it.
            await DataBarrier.LockExclusiveAsync(db, tenantId, ct);
            var tenant = await db.Tenants.Include(t => t.ApiKeys).SingleOrDefaultAsync(t => t.Id == tenantId, ct);
            if (tenant is null)
            {
                return ServiceError.NotFound();
            }

            if (await db.TenantErasures.AsNoTracking().SingleOrDefaultAsync(e => e.TenantId == tenantId, ct) is { } existing)
            {
                return new ErasureRequestOutcome(existing, Replayed: true);
            }

            var now = tenants.Now();
            if (asOperator)
            {
                if (await db.Tenants.AnyAsync(t => t.ParentTenantId == tenantId && t.Status != TenantStatus.Deleted, ct))
                {
                    return ServiceError.Conflict("tenant_has_children", "Delete the tenant's subtenants before erasing it.");
                }

                if (tenant.Status != TenantStatus.Deleted)
                {
                    await tenants.MarkDeletedAsync(tenant, actor, ct);
                }
            }
            else if (tenant.Status != TenantStatus.Deleted)
            {
                return ServiceError.Conflict("tenant_not_deleted", "Only a deleted subtenant can be erased; delete it first.");
            }
            else if (tenant.DeletedAt + options.Value.MinimumAgeAfterDeletion > now)
            {
                return ServiceError.Conflict("erasure_too_early",
                    $"The subtenant can be erased from {tenant.DeletedAt + options.Value.MinimumAgeAfterDeletion:u}, "
                    + "so a deletion by mistake can still be inspected.");
            }

            tenant.ErasureRequestedAt = now;
            tenant.UpdatedAt = now;
            var requestedBy = PaymentLedger.Label(actor);
            var job = await jobs.QueueNextAsync(BackgroundJobTypes.EraseTenant, tenantId, requestedBy, ct);
            var erasure = new TenantErasure
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                RequestedAt = now,
                RequestedBy = requestedBy,
                Reason = reason!.Trim(),
                IdempotencyKeySha256 = idempotencyKey is null ? null : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey))),
                JobId = job.Id,
            };
            db.TenantErasures.Add(erasure);

            try
            {
                await db.SaveChangesAsync(ct);
                await AbandonQueuedDeliveriesAsync(tenantId, now, ct);
                await DisableEndpointAsync(tenantId, now, ct);
                await transaction.CommitAsync(ct);
                return new ErasureRequestOutcome(erasure, Replayed: false);
            }
            catch (DbUpdateException ex) when (attempt < 3
                && TenantService.IsUniqueViolation(ex, TenantErasureConfiguration.TenantUniqueIndex))
            {
                // A concurrent request won; the next attempt returns it.
            }
            catch (DbUpdateConcurrencyException)
            {
                return ServiceError.Conflict("concurrent_modification", "The tenant was changed concurrently; retry the request.");
            }
        }
    }

    /// <summary>Queued work for the tenant's events (at the parent's endpoint too) and for its own endpoint stops.</summary>
    private Task<int> AbandonQueuedDeliveriesAsync(Guid tenantId, DateTimeOffset now, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"""
            UPDATE webhook_deliveries d SET state = 'abandoned', hold_reason = NULL, due_at = NULL, last_outcome = 'abandoned',
                abandon_reason = {ErasureRules.AbandonReason}, updated_at = {now}, version = d.version + 1
            WHERE d.state IN ('pending', 'retry_wait', 'held', 'failed')
              AND (d.owner_tenant_id = {tenantId} OR d.endpoint_id IN (SELECT e.id FROM webhook_endpoints e WHERE e.tenant_id = {tenantId}))
            """, ct);

    private async Task DisableEndpointAsync(Guid tenantId, DateTimeOffset now, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync($"""
            UPDATE webhook_endpoints SET state = 'paused', updated_at = {now}, version = version + 1
            WHERE tenant_id = {tenantId} AND state <> 'paused'
            """, ct);
        await db.Database.ExecuteSqlAsync($"""
            UPDATE webhook_signing_keys SET state = 'retired', protected_secret = NULL,
                retires_at = coalesce(retires_at, {now}), retired_at = coalesce(retired_at, {now})
            WHERE endpoint_id IN (SELECT e.id FROM webhook_endpoints e WHERE e.tenant_id = {tenantId})
              AND (state <> 'retired' OR protected_secret IS NOT NULL)
            """, ct);
    }
}
