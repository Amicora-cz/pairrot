using System.Text.Json;
using Pairrot.Core.Tenants;
using Pairrot.Infrastructure.Tenants;
using Pairrot.Infrastructure.Users;

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

public sealed record AntiforgeryResponse(string Token, string HeaderName);

public sealed record UserResponse(Guid Id, string Email);

public sealed record SessionInfoResponse(
    DateTimeOffset CreatedAt, DateTimeOffset LastActivityAt, DateTimeOffset IdleExpiresAt, DateTimeOffset AbsoluteExpiresAt);

/// <param name="Blocked">The tenant or its parent is suspended; operations for it are refused.</param>
public sealed record MembershipResponse(string Role, bool Blocked, TenantResponse Tenant);

/// <summary>The signed-in user and their access. Never contains the cookie or any secret.</summary>
public sealed record SessionSummaryResponse(
    UserResponse User,
    SessionInfoResponse Session,
    Guid? SelectedTenantId,
    IReadOnlyList<MembershipResponse> Memberships)
{
    public static SessionSummaryResponse From(SessionSummary summary)
    {
        var session = summary.Current.Session;
        return new SessionSummaryResponse(
            new UserResponse(session.UserId, summary.Current.Email),
            new SessionInfoResponse(session.CreatedAt, session.LastActivityAt, session.IdleExpiresAt, session.AbsoluteExpiresAt),
            summary.Current.Tenant?.Id,
            summary.Memberships.Select(m => new MembershipResponse(m.Membership.Role, m.Blocked, TenantResponse.From(m.Tenant))).ToList());
    }
}

/// <param name="Type"><c>user</c>, <c>api_key</c>, or <c>administrator</c>.</param>
public sealed record AuditActorResponse(string Type, Guid? UserId, string? Email, Guid? ApiKeyId, string? Description);

public sealed record AuditEventResponse(
    Guid Id, Guid TenantId, string Action, AuditActorResponse Actor, JsonElement Details, DateTimeOffset CreatedAt)
{
    public static AuditEventResponse From(AuditEventView view)
    {
        var e = view.Event;
        var actor = e switch
        {
            { ActorUserId: { } userId } => new AuditActorResponse("user", userId, view.ActorUserEmail, null, null),
            { ActorApiKeyId: { } keyId } => new AuditActorResponse("api_key", null, null, keyId, null),
            _ => new AuditActorResponse("administrator", null, null, null, e.ActorDescription),
        };

        return new AuditEventResponse(
            e.Id, e.TargetTenantId, e.Action, actor, JsonSerializer.Deserialize<JsonElement>(e.Details), e.CreatedAt);
    }
}
