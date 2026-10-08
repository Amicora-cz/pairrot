using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pairrot.Core.ApiKeys;
using Pairrot.Core.Banking;
using Pairrot.Core.Tenants;
using Pairrot.Infrastructure.Persistence;

namespace Pairrot.Infrastructure.Tenants;

/// <summary>Changes requested by a PATCH; absent fields stay unchanged.</summary>
public sealed record TenantChange(
    Optional<string?> Name = default,
    Optional<string?> ContactEmail = default,
    Optional<TenantStatus> Status = default);

public sealed record Page<T>(IReadOnlyList<T> Items, Guid? NextCursor);

/// <summary>An audit event with the email of the user who performed it, if any.</summary>
public sealed record AuditEventView(TenantAuditEvent Event, string? ActorUserEmail);

/// <summary>
/// Tenant operations. Every method is scoped either to the caller's own tenant or to the
/// direct children of an owner; callers are authorized by the API layer before calling.
/// </summary>
public class TenantService(PairrotDbContext db, TimeProvider time)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    internal static readonly JsonSerializerOptions AuditJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public Task<Tenant?> FindAsync(Guid tenantId, CancellationToken ct = default) =>
        db.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Id == tenantId, ct);

    /// <summary>The caller updates its own configuration (name and contact email only).</summary>
    public async Task<ServiceResult<Tenant>> UpdateOwnConfigurationAsync(
        Guid tenantId, TenantChange change, Actor actor, CancellationToken ct = default)
    {
        if (change.Status.HasValue)
        {
            return ServiceError.Validation("status cannot be changed through this endpoint.");
        }

        var tenant = await db.Tenants.SingleOrDefaultAsync(t => t.Id == tenantId, ct);
        return tenant is null ? ServiceError.NotFound() : await ApplyChangeAsync(tenant, change, actor, ct);
    }

    public async Task<ServiceResult<IssuedApiKey>> CreateSubtenantAsync(
        Guid ownerId, string? externalId, string? name, string? contactEmail, Actor actor, CancellationToken ct = default)
    {
        var error = TenantRules.ValidateExternalId(externalId)
            ?? TenantRules.ValidateName(name)
            ?? TenantRules.ValidateContactEmail(contactEmail);
        if (error is not null)
        {
            return ServiceError.Validation(error);
        }

        externalId = externalId!.Trim();

        var owner = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Id == ownerId, ct);
        if (owner is null || !owner.IsRoot || !owner.CanCreateSubtenants || owner.Status != TenantStatus.Active)
        {
            // Authorization should have stopped this earlier; never create grandchildren.
            return ServiceError.NotFound();
        }

        if (await db.Tenants.AnyAsync(t => t.ParentTenantId == ownerId && t.ExternalId == externalId, ct))
        {
            return DuplicateExternalId();
        }

        var now = Now();
        var child = new Tenant
        {
            Id = Guid.CreateVersion7(),
            ParentTenantId = ownerId,
            ExternalId = externalId,
            Name = name!.Trim(),
            ContactEmail = contactEmail?.Trim(),
            Status = TenantStatus.Active,
            CanCreateSubtenants = false,
            CanCreateCampaigns = false,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Tenants.Add(child);
        AddAudit(actor, child.Id, AuditActions.TenantCreated, new { child.ExternalId, ParentTenantId = ownerId });
        var issued = AddKey(child.Id, ApiKeyPurpose.Tenant, label: null, actor);

        try
        {
            // Tenant, initial key, and audit records are written in one transaction.
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex, "tenants_parent_external_id_unique"))
        {
            db.ChangeTracker.Clear();
            return DuplicateExternalId();
        }

        issued.Key.Tenant = child;
        return issued;
    }

    public async Task<Page<Tenant>> ListSubtenantsAsync(
        Guid ownerId, string? externalId, TenantStatus? status, int limit, Guid? after, CancellationToken ct = default)
    {
        var query = db.Tenants.AsNoTracking().Where(t => t.ParentTenantId == ownerId);

        query = status is { } s
            ? query.Where(t => t.Status == s)
            : query.Where(t => t.Status != TenantStatus.Deleted);

        if (externalId is not null)
        {
            query = query.Where(t => t.ExternalId == externalId);
        }

        if (after is { } cursor)
        {
            query = query.Where(t => t.Id.CompareTo(cursor) > 0);
        }

        // IDs are UUIDv7, so ordering by ID is ordering by creation time.
        return await ToPageAsync(query.OrderBy(t => t.Id), t => t.Id, limit, ct);
    }

    public Task<Tenant?> FindSubtenantAsync(Guid ownerId, Guid childId, CancellationToken ct = default) =>
        db.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Id == childId && t.ParentTenantId == ownerId, ct);

    public async Task<ServiceResult<Tenant>> UpdateSubtenantAsync(
        Guid ownerId, Guid childId, TenantChange change, Actor actor, CancellationToken ct = default)
    {
        if (change.Status is { HasValue: true, Value: TenantStatus.Deleted })
        {
            return ServiceError.Validation("status must be 'active' or 'suspended'; use DELETE to delete a subtenant.");
        }

        var child = await db.Tenants.SingleOrDefaultAsync(t => t.Id == childId && t.ParentTenantId == ownerId, ct);
        return child is null ? ServiceError.NotFound() : await ApplyChangeAsync(child, change, actor, ct);
    }

    /// <summary>Marks a child deleted and revokes all its keys. Repeating it has no further effect.</summary>
    public async Task<ServiceResult<bool>> DeleteSubtenantAsync(
        Guid ownerId, Guid childId, Actor actor, CancellationToken ct = default)
    {
        var child = await db.Tenants
            .Include(t => t.ApiKeys)
            .SingleOrDefaultAsync(t => t.Id == childId && t.ParentTenantId == ownerId, ct);
        if (child is null)
        {
            return ServiceError.NotFound();
        }

        if (child.Status == TenantStatus.Deleted)
        {
            return true;
        }

        await MarkDeletedAsync(child, actor, ct);
        return await SaveAsync(true, ct);
    }

    /// <summary>
    /// Marks a tracked tenant (with its keys loaded) deleted, revokes its keys, and archives its
    /// active connections; the caller saves. Shared by DELETE and the operator's erasure (Feature 014).
    /// </summary>
    internal async Task MarkDeletedAsync(Tenant tenant, Actor actor, CancellationToken ct)
    {
        var now = Now();
        var revoked = 0;
        foreach (var key in tenant.ApiKeys.Where(k => k.RevokedAt is null))
        {
            key.RevokedAt = now;
            revoked++;
        }

        var previousStatus = tenant.Status;
        tenant.Status = TenantStatus.Deleted;
        tenant.DeletedAt = now;
        tenant.UpdatedAt = now;

        // Feature 013: deletion releases the tenant's accounts, so a new registration may use them.
        var released = await db.BankConnections
            .Where(b => b.TenantId == tenant.Id && b.Status == BankConnectionStatus.Active)
            .ToListAsync(ct);
        foreach (var connection in released)
        {
            connection.Status = BankConnectionStatus.Archived;
            connection.ArchivedAt = now;
            connection.ArchivedReason = BankConnectionArchivedReason.TenantDeleted;
            connection.UpdatedAt = now;
            AddAudit(actor, tenant.Id, AuditActions.BankConnectionArchived, new { ConnectionId = connection.Id, Reason = "tenant_deleted" });
        }

        AddAudit(actor, tenant.Id, AuditActions.TenantDeleted, new
        {
            From = Lower(previousStatus),
            RevokedKeyCount = revoked,
            ArchivedBankConnectionCount = released.Count,
        });
    }

    public async Task<ServiceResult<IReadOnlyList<TenantApiKey>>> ListSubtenantKeysAsync(
        Guid ownerId, Guid childId, CancellationToken ct = default)
    {
        if (await FindSubtenantAsync(ownerId, childId, ct) is null)
        {
            return ServiceError.NotFound();
        }

        return await db.ApiKeys.AsNoTracking()
            .Where(k => k.TenantId == childId)
            .OrderBy(k => k.CreatedAt).ThenBy(k => k.Id)
            .ToListAsync(ct);
    }

    /// <summary>Issues a tenant key for an active or suspended child. Never issues management keys.</summary>
    public async Task<ServiceResult<IssuedApiKey>> IssueSubtenantKeyAsync(
        Guid ownerId, Guid childId, string? label, Actor actor, CancellationToken ct = default)
    {
        if (TenantRules.ValidateLabel(label) is { } error)
        {
            return ServiceError.Validation(error);
        }

        var child = await FindSubtenantAsync(ownerId, childId, ct);
        if (child is null)
        {
            return ServiceError.NotFound();
        }

        if (child.Status == TenantStatus.Deleted)
        {
            return TenantDeleted(child);
        }

        var issued = AddKey(child.Id, ApiKeyPurpose.Tenant, label, actor);
        return await SaveAsync(issued, ct);
    }

    /// <summary>Revokes a child's key. Repeating it has no further effect.</summary>
    public async Task<ServiceResult<bool>> RevokeSubtenantKeyAsync(
        Guid ownerId, Guid childId, Guid keyId, Actor actor, CancellationToken ct = default)
    {
        var key = await db.ApiKeys.SingleOrDefaultAsync(
            k => k.Id == keyId && k.TenantId == childId && k.Tenant!.ParentTenantId == ownerId, ct);
        return key is null ? ServiceError.NotFound() : await RevokeAsync(key, actor, ct);
    }

    /// <summary>Change history of a direct child, newest first.</summary>
    public async Task<ServiceResult<Page<AuditEventView>>> ListSubtenantAuditEventsAsync(
        Guid ownerId, Guid childId, int limit, Guid? before, CancellationToken ct = default)
    {
        if (await FindSubtenantAsync(ownerId, childId, ct) is null)
        {
            return ServiceError.NotFound();
        }

        return await ListAuditEventsAsync(childId, limit, before, ct);
    }

    public async Task<IReadOnlyList<TenantApiKey>> ListOwnKeysAsync(Guid tenantId, CancellationToken ct = default) =>
        await db.ApiKeys.AsNoTracking()
            .Where(k => k.TenantId == tenantId)
            .OrderBy(k => k.CreatedAt).ThenBy(k => k.Id)
            .ToListAsync(ct);

    /// <summary>
    /// Issues a key for the caller's own tenant. A management key requires a root tenant that can
    /// create subtenants, under the same hierarchy rules as Feature 001.
    /// </summary>
    public async Task<ServiceResult<IssuedApiKey>> IssueOwnKeyAsync(
        Guid tenantId, ApiKeyPurpose purpose, string? label, Actor actor, CancellationToken ct = default)
    {
        if (TenantRules.ValidateLabel(label) is { } error)
        {
            return ServiceError.Validation(error);
        }

        var tenant = await FindAsync(tenantId, ct);
        if (tenant is null)
        {
            return ServiceError.NotFound();
        }

        if (tenant.Status == TenantStatus.Deleted)
        {
            return TenantDeleted(tenant);
        }

        if (purpose == ApiKeyPurpose.Management && !(tenant.IsRoot && tenant.CanCreateSubtenants))
        {
            return ServiceError.Forbidden("Management keys require a root tenant that can create subtenants.");
        }

        var issued = AddKey(tenantId, purpose, label, actor);
        return await SaveAsync(issued, ct);
    }

    /// <summary>Revokes one of the caller's own keys. Repeating it has no further effect.</summary>
    public async Task<ServiceResult<bool>> RevokeOwnKeyAsync(
        Guid tenantId, Guid keyId, Actor actor, CancellationToken ct = default)
    {
        var key = await db.ApiKeys.SingleOrDefaultAsync(k => k.Id == keyId && k.TenantId == tenantId, ct);
        return key is null ? ServiceError.NotFound() : await RevokeAsync(key, actor, ct);
    }

    /// <summary>Change history of one tenant, newest first.</summary>
    public async Task<Page<AuditEventView>> ListAuditEventsAsync(
        Guid tenantId, int limit, Guid? before, CancellationToken ct = default)
    {
        var events = db.AuditEvents.AsNoTracking().Where(e => e.TargetTenantId == tenantId);
        if (before is { } cursor)
        {
            events = events.Where(e => e.Id.CompareTo(cursor) < 0);
        }

        var query =
            from e in events
            join u in db.Users on e.ActorUserId equals (Guid?)u.Id into actors
            from u in actors.DefaultIfEmpty()
            orderby e.Id descending
            select new AuditEventView(e, u != null ? u.Email : null);

        return await ToPageAsync(query, v => v.Event.Id, limit, ct);
    }

    internal IssuedApiKey AddKey(Guid tenantId, ApiKeyPurpose purpose, string? label, Actor actor)
    {
        var keyId = Guid.CreateVersion7();
        var (secret, hash) = ApiKeySecret.Generate(keyId);
        var key = new TenantApiKey
        {
            Id = keyId,
            TenantId = tenantId,
            Purpose = purpose,
            Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim(),
            SecretHash = hash,
            CreatedAt = Now(),
        };
        db.ApiKeys.Add(key);
        AddAudit(actor, tenantId, AuditActions.ApiKeyIssued, new { KeyId = keyId, Purpose = Lower(purpose), key.Label });
        return new IssuedApiKey(key, secret);
    }

    internal void AddAudit(Actor actor, Guid targetTenantId, string action, object details) =>
        db.AuditEvents.Add(new TenantAuditEvent
        {
            Id = Guid.CreateVersion7(),
            ActorUserId = actor.UserId,
            ActorApiKeyId = actor.ApiKeyId,
            ActorDescription = actor.Description,
            TargetTenantId = targetTenantId,
            Action = action,
            Details = JsonSerializer.Serialize(details, AuditJson),
            CreatedAt = Now(),
        });

    /// <summary>Current UTC time truncated to PostgreSQL timestamptz precision (microseconds).</summary>
    internal DateTimeOffset Now()
    {
        var now = time.GetUtcNow();
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMicrosecond));
    }

    internal static string Lower<TEnum>(TEnum value) where TEnum : struct, Enum => value.ToString().ToLowerInvariant();

    internal static bool IsUniqueViolation(DbUpdateException ex, string constraint) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg
        && pg.ConstraintName == constraint;

    private async Task<ServiceResult<Tenant>> ApplyChangeAsync(
        Tenant tenant, TenantChange change, Actor actor, CancellationToken ct)
    {
        if (tenant.Status == TenantStatus.Deleted)
        {
            return TenantDeleted(tenant);
        }

        if (change.Name.HasValue && TenantRules.ValidateName(change.Name.Value) is { } nameError)
        {
            return ServiceError.Validation(nameError);
        }

        if (change.ContactEmail.HasValue && TenantRules.ValidateContactEmail(change.ContactEmail.Value) is { } emailError)
        {
            return ServiceError.Validation(emailError);
        }

        var changedFields = new List<string>();
        if (change.Name.HasValue && change.Name.Value!.Trim() != tenant.Name)
        {
            tenant.Name = change.Name.Value.Trim();
            changedFields.Add("name");
        }

        if (change.ContactEmail.HasValue && change.ContactEmail.Value?.Trim() != tenant.ContactEmail)
        {
            tenant.ContactEmail = change.ContactEmail.Value?.Trim();
            changedFields.Add("contact_email");
        }

        if (changedFields.Count > 0)
        {
            AddAudit(actor, tenant.Id, AuditActions.TenantConfigurationUpdated, new { ChangedFields = changedFields });
        }

        var statusChanged = change.Status.HasValue && change.Status.Value != tenant.Status;
        if (statusChanged)
        {
            AddAudit(actor, tenant.Id, AuditActions.TenantStatusChanged,
                new { From = Lower(tenant.Status), To = Lower(change.Status.Value) });
            tenant.Status = change.Status.Value;
        }

        if (changedFields.Count == 0 && !statusChanged)
        {
            return tenant;
        }

        tenant.UpdatedAt = Now();
        return await SaveAsync(tenant, ct);
    }

    private async Task<ServiceResult<bool>> RevokeAsync(TenantApiKey key, Actor actor, CancellationToken ct)
    {
        if (key.RevokedAt is not null)
        {
            return true;
        }

        key.RevokedAt = Now();
        AddAudit(actor, key.TenantId, AuditActions.ApiKeyRevoked, new { KeyId = key.Id });
        return await SaveAsync(true, ct);
    }

    /// <summary>Reads one item more than requested to know whether another page exists.</summary>
    private static async Task<Page<T>> ToPageAsync<T>(
        IQueryable<T> ordered, Func<T, Guid> id, int limit, CancellationToken ct)
    {
        var items = await ordered.Take(limit + 1).ToListAsync(ct);
        var hasMore = items.Count > limit;
        if (hasMore)
        {
            items.RemoveAt(items.Count - 1);
        }

        return new Page<T>(items, hasMore ? id(items[^1]) : null);
    }

    private async Task<ServiceResult<T>> SaveAsync<T>(T value, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return value;
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return ServiceError.Conflict("concurrent_modification", "The tenant was changed concurrently; retry the request.");
        }
    }

    private static ServiceError DuplicateExternalId() =>
        ServiceError.Conflict("duplicate_external_id", "A subtenant with this external_id already exists.");

    /// <summary><c>tenant_deleted</c>, or <c>tenant_erased</c> once erasure was requested (Feature 014).</summary>
    internal static ServiceError TenantDeleted(Tenant tenant, string message = "The tenant is deleted and cannot be changed.") =>
        tenant.ErasureRequestedAt is not null ? TenantErased() : ServiceError.Conflict("tenant_deleted", message);

    internal static ServiceError TenantErased() =>
        ServiceError.Conflict("tenant_erased", "The tenant is erased; its records are history only and nothing is created for it.");
}
