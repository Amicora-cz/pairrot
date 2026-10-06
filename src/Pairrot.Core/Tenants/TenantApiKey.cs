namespace Pairrot.Core.Tenants;

public class TenantApiKey
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }
    public ApiKeyPurpose Purpose { get; set; }
    public string? Label { get; set; }
    public required byte[] SecretHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public enum ApiKeyPurpose
{
    Tenant,
    Management,
}
