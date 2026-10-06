using Pairrot.Core.Tenants;
using Pairrot.Infrastructure.Tenants;

namespace Pairrot.Api.Http;

// Serialized as snake_case JSON (see Program.cs). Key secrets appear only in IssuedApiKeyResponse.

public sealed record CapabilitiesResponse(bool CanCreateSubtenants, bool CanCreateCampaigns);

public sealed record ConfigurationResponse(string Name, string? ContactEmail)
{
    public static ConfigurationResponse From(Tenant tenant) => new(tenant.Name, tenant.ContactEmail);
}

public sealed record TenantResponse(
    Guid Id,
    Guid? ParentTenantId,
    string ExternalId,
    TenantStatus Status,
    ConfigurationResponse Configuration,
    CapabilitiesResponse Capabilities,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? DeletedAt)
{
    public static TenantResponse From(Tenant tenant) => new(
        tenant.Id,
        tenant.ParentTenantId,
        tenant.ExternalId,
        tenant.Status,
        ConfigurationResponse.From(tenant),
        new CapabilitiesResponse(tenant.CanCreateSubtenants, tenant.CanCreateCampaigns),
        tenant.CreatedAt,
        tenant.UpdatedAt,
        tenant.DeletedAt);
}

public sealed record ApiKeyResponse(
    Guid Id, string? Label, ApiKeyPurpose Purpose, DateTimeOffset CreatedAt, DateTimeOffset? RevokedAt)
{
    public static ApiKeyResponse From(TenantApiKey key) => new(key.Id, key.Label, key.Purpose, key.CreatedAt, key.RevokedAt);
}

/// <summary>Returned only when a key is created; the secret cannot be retrieved again.</summary>
public sealed record IssuedApiKeyResponse(
    Guid Id, string? Label, ApiKeyPurpose Purpose, DateTimeOffset CreatedAt, string Secret)
{
    public static IssuedApiKeyResponse From(IssuedApiKey issued) =>
        new(issued.Key.Id, issued.Key.Label, issued.Key.Purpose, issued.Key.CreatedAt, issued.Secret);
}

public sealed record CreatedSubtenantResponse(TenantResponse Tenant, IssuedApiKeyResponse ApiKey);

public sealed record ListResponse<T>(IReadOnlyList<T> Items, Guid? NextCursor);
