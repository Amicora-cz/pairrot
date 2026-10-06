namespace Pairrot.Core.Tenants;

public class TenantAuditEvent
{
    public Guid Id { get; set; }
    public Guid? ActorApiKeyId { get; set; }
    public string? ActorDescription { get; set; }
    public Guid TargetTenantId { get; set; }
    public required string Action { get; set; }

    /// <summary>JSON object with change details. Never contains secrets or secret hashes.</summary>
    public string Details { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; }
}

public static class AuditActions
{
    public const string TenantCreated = "tenant.created";
    public const string TenantConfigurationUpdated = "tenant.configuration_updated";
    public const string TenantStatusChanged = "tenant.status_changed";
    public const string TenantDeleted = "tenant.deleted";
    public const string ApiKeyIssued = "api_key.issued";
    public const string ApiKeyRevoked = "api_key.revoked";
}
