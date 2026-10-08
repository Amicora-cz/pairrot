namespace Pairrot.Core.Tenants;

/// <summary>
/// A root tenant or a subtenant. Subtenants are the same entity with a parent assigned.
/// </summary>
public class Tenant
{
    public Guid Id { get; set; }
    public Guid? ParentTenantId { get; set; }
    public Tenant? ParentTenant { get; set; }
    public required string ExternalId { get; set; }
    public required string Name { get; set; }
    public string? ContactEmail { get; set; }
    public TenantStatus Status { get; set; } = TenantStatus.Active;
    public bool CanCreateSubtenants { get; set; }
    public bool CanCreateCampaigns { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }

    /// <summary>
    /// Feature 014: when erasure was requested. From then on this is the tenant's permanent
    /// collection barrier: nothing new about it is collected, only minimized tombstones.
    /// </summary>
    public DateTimeOffset? ErasureRequestedAt { get; set; }

    /// <summary>When the erasure finished; the name, contact, and external ID are placeholders since.</summary>
    public DateTimeOffset? ErasedAt { get; set; }

    public List<TenantApiKey> ApiKeys { get; set; } = [];

    public bool IsRoot => ParentTenantId is null;
}
