using Microsoft.EntityFrameworkCore;
using Pairrot.Core.Tenants;
using Pairrot.Infrastructure.Persistence;

namespace Pairrot.Infrastructure.Tenants;

public sealed record RootSetupResult(
    Tenant Tenant,
    bool Created,
    IssuedApiKey? TenantKey,
    IssuedApiKey? ManagementKey,
    bool ContactEmailIgnored);

/// <summary>
/// Administrative operations on root tenants. Not exposed over HTTP; used by Pairrot.Admin.
/// </summary>
public class RootTenantAdministration(PairrotDbContext db, TenantService tenants)
{
    public const string NaLekciExternalId = "nalekci.cz";

    /// <summary>
    /// Creates the root tenant with one tenant key and one management key. If it already exists,
    /// nothing is changed and no keys are issued.
    /// </summary>
    public async Task<ServiceResult<RootSetupResult>> SetupAsync(
        string externalId, string name, string? contactEmail, string operatorDescription, CancellationToken ct = default)
    {
        var error = TenantRules.ValidateExternalId(externalId)
            ?? TenantRules.ValidateName(name)
            ?? TenantRules.ValidateContactEmail(contactEmail);
        if (error is not null)
        {
            return ServiceError.Validation(error);
        }

        var existing = await FindRootAsync(externalId, ct);
        if (existing is not null)
        {
            return Existing(existing, contactEmail);
        }

        var now = tenants.Now();
        var root = new Tenant
        {
            Id = Guid.CreateVersion7(),
            ExternalId = externalId.Trim(),
            Name = name.Trim(),
            ContactEmail = contactEmail?.Trim(),
            Status = TenantStatus.Active,
            CanCreateSubtenants = true,
            CanCreateCampaigns = false,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var actor = Actor.Administrator(operatorDescription);
        db.Tenants.Add(root);
        tenants.AddAudit(actor, root.Id, AuditActions.TenantCreated, new { root.ExternalId, Root = true });
        var tenantKey = tenants.AddKey(root.Id, ApiKeyPurpose.Tenant, "initial tenant key", actor);
        var managementKey = tenants.AddKey(root.Id, ApiKeyPurpose.Management, "initial management key", actor);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (TenantService.IsUniqueViolation(ex, "tenants_root_external_id_unique"))
        {
            // A concurrent setup won; report its tenant without issuing keys.
            db.ChangeTracker.Clear();
            return Existing((await FindRootAsync(externalId, ct))!, contactEmail);
        }

        return new RootSetupResult(root, true, tenantKey, managementKey, false);
    }

    public async Task<ServiceResult<IssuedApiKey>> IssueRootKeyAsync(
        string externalId, ApiKeyPurpose purpose, string? label, string operatorDescription, CancellationToken ct = default)
    {
        if (TenantRules.ValidateLabel(label) is { } error)
        {
            return ServiceError.Validation(error);
        }

        var root = await FindRootAsync(externalId, ct);
        if (root is null)
        {
            return ServiceError.NotFound($"Root tenant '{externalId}' does not exist. Run setup first.");
        }

        var issued = tenants.AddKey(root.Id, purpose, label, Actor.Administrator(operatorDescription));
        await db.SaveChangesAsync(ct);
        return issued;
    }

    public async Task<ServiceResult<bool>> RevokeRootKeyAsync(
        string externalId, Guid keyId, string operatorDescription, CancellationToken ct = default)
    {
        var key = await db.ApiKeys.SingleOrDefaultAsync(
            k => k.Id == keyId && k.Tenant!.ParentTenantId == null && k.Tenant.ExternalId == externalId, ct);
        if (key is null)
        {
            return ServiceError.NotFound($"Key {keyId} does not belong to root tenant '{externalId}'.");
        }

        if (key.RevokedAt is null)
        {
            key.RevokedAt = tenants.Now();
            tenants.AddAudit(Actor.Administrator(operatorDescription), key.TenantId, AuditActions.ApiKeyRevoked,
                new { KeyId = key.Id });
            await db.SaveChangesAsync(ct);
        }

        return true;
    }

    public async Task<ServiceResult<IReadOnlyList<TenantApiKey>>> ListRootKeysAsync(
        string externalId, CancellationToken ct = default)
    {
        var root = await FindRootAsync(externalId, ct);
        if (root is null)
        {
            return ServiceError.NotFound($"Root tenant '{externalId}' does not exist. Run setup first.");
        }

        return await db.ApiKeys.AsNoTracking()
            .Where(k => k.TenantId == root.Id)
            .OrderBy(k => k.CreatedAt).ThenBy(k => k.Id)
            .ToListAsync(ct);
    }

    private Task<Tenant?> FindRootAsync(string externalId, CancellationToken ct) =>
        db.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.ParentTenantId == null && t.ExternalId == externalId.Trim(), ct);

    private static RootSetupResult Existing(Tenant tenant, string? contactEmail) =>
        new(tenant, false, null, null,
            ContactEmailIgnored: contactEmail is not null && contactEmail.Trim() != tenant.ContactEmail);
}
