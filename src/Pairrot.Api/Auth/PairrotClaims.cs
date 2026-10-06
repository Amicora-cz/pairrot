using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Pairrot.Core.Tenants;
using Pairrot.Infrastructure.Tenants;
using Pairrot.Infrastructure.Users;

namespace Pairrot.Api.Auth;

/// <summary>
/// Claims describe the caller's current state. They are rebuilt from the database on every
/// request for both API keys and sessions; nothing here is trusted from an earlier request.
/// </summary>
public static class PairrotClaims
{
    public const string AuthMethod = "pairrot:auth_method";
    public const string TenantId = "pairrot:tenant_id";
    public const string ApiKeyId = "pairrot:api_key_id";
    public const string Purpose = "pairrot:purpose";
    public const string IsRoot = "pairrot:is_root";
    public const string CanCreateSubtenants = "pairrot:can_create_subtenants";
    public const string Blocked = "pairrot:blocked";

    /// <summary>Stable user identifier; antiforgery tokens are bound to it.</summary>
    public const string UserId = ClaimTypes.NameIdentifier;

    public const string SessionId = "pairrot:session_id";
    public const string SelectedTenantId = "pairrot:selected_tenant_id";

    public const string ApiKeyMethod = "api_key";
    public const string SessionMethod = "session";

    public static Guid GetTenantId(this ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(TenantId)!);

    public static Actor GetActor(this ClaimsPrincipal user) => user.IsSession()
        ? Actor.User(user.GetUserId())
        : Actor.Key(Guid.Parse(user.FindFirstValue(ApiKeyId)!));

    public static bool IsBlocked(this ClaimsPrincipal user) => Flag(user, Blocked);

    public static bool IsSession(this ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true && user.FindFirstValue(AuthMethod) == SessionMethod;

    /// <summary>Whether the caller may act for a tenant right now (a valid key, or a current membership).</summary>
    public static bool HasTenant(this ClaimsPrincipal user) => user.HasClaim(c => c.Type == TenantId);

    public static Guid GetUserId(this ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(UserId)!);

    public static Guid GetSessionId(this ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(SessionId)!);

    public static Guid? GetSelectedTenantId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(SelectedTenantId), out var id) ? id : null;

    /// <summary>The minimal identity stored in the encrypted cookie: which session row to check.</summary>
    public static ClaimsPrincipal SessionTicket(Guid userId, Guid sessionId) =>
        new(new ClaimsIdentity(
        [
            new Claim(UserId, userId.ToString()),
            new Claim(SessionId, sessionId.ToString()),
        ], PairrotAuth.SessionScheme));

    /// <summary>The per-request identity of a validated session.</summary>
    public static ClaimsPrincipal ForSession(ValidatedSession session)
    {
        var claims = new List<Claim>
        {
            new(AuthMethod, SessionMethod),
            new(UserId, session.Session.UserId.ToString()),
            new(SessionId, session.Session.Id.ToString()),
        };

        if (session.Session.SelectedTenantId is { } selected)
        {
            claims.Add(new Claim(SelectedTenantId, selected.ToString()));
        }

        // Tenant claims only while the membership is active and the tenant is not deleted.
        if (session.Tenant is { } tenant)
        {
            claims.Add(new Claim(TenantId, tenant.Id.ToString()));
            claims.Add(new Claim(IsRoot, tenant.IsRoot.ToString()));
            claims.Add(new Claim(CanCreateSubtenants, tenant.CanCreateSubtenants.ToString()));
            claims.Add(new Claim(Blocked, session.Blocked.ToString()));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, PairrotAuth.SessionScheme));
    }

    private static bool Flag(ClaimsPrincipal user, string type) =>
        bool.TryParse(user.FindFirstValue(type), out var value) && value;

    /// <summary>A tenant key (or management key) of an active tenant, or a session with a current membership.</summary>
    private static bool CanActForTenant(ClaimsPrincipal user, ApiKeyPurpose keyPurpose) =>
        user.HasTenant()
        && !user.IsBlocked()
        && (user.IsSession() || user.FindFirstValue(Purpose) == keyPurpose.ToString());

    public static class Policies
    {
        /// <summary>Current-tenant endpoints: a tenant key, or a session with the tenant selected.</summary>
        public const string TenantAccess = "TenantAccess";

        /// <summary>Child management: a management key, or a session of a parent administrator.</summary>
        public const string TenantManagement = "TenantManagement";

        /// <summary>Browser-only endpoints; API keys are not accepted.</summary>
        public const string Session = "Session";

        public static void Configure(AuthorizationOptions options)
        {
            options.AddPolicy(TenantAccess, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(ctx => CanActForTenant(ctx.User, ApiKeyPurpose.Tenant)));

            // The capability is checked on every request; only roots may have children (one level).
            options.AddPolicy(TenantManagement, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(ctx => CanActForTenant(ctx.User, ApiKeyPurpose.Management)
                    && Flag(ctx.User, CanCreateSubtenants)
                    && Flag(ctx.User, IsRoot)));

            options.AddPolicy(Session, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(ctx => ctx.User.IsSession()));
        }
    }
}
