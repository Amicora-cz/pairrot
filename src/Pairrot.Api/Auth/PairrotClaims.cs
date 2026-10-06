using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Pairrot.Core.Tenants;
using Pairrot.Infrastructure.Tenants;

namespace Pairrot.Api.Auth;

public static class PairrotClaims
{
    public const string TenantId = "pairrot:tenant_id";
    public const string ApiKeyId = "pairrot:api_key_id";
    public const string Purpose = "pairrot:purpose";
    public const string IsRoot = "pairrot:is_root";
    public const string CanCreateSubtenants = "pairrot:can_create_subtenants";
    public const string Blocked = "pairrot:blocked";

    public static Guid GetTenantId(this ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(TenantId)!);

    public static Actor GetActor(this ClaimsPrincipal user) => Actor.Key(Guid.Parse(user.FindFirstValue(ApiKeyId)!));

    public static bool IsBlocked(this ClaimsPrincipal user) => Flag(user, Blocked);

    private static bool Flag(ClaimsPrincipal user, string type) =>
        bool.TryParse(user.FindFirstValue(type), out var value) && value;

    private static bool HasPurpose(ClaimsPrincipal user, ApiKeyPurpose purpose) =>
        user.FindFirstValue(Purpose) == purpose.ToString();

    public static class Policies
    {
        public const string TenantKey = "TenantKey";
        public const string ManagementKey = "ManagementKey";

        public static void Configure(AuthorizationOptions options)
        {
            // A tenant key addresses only its own tenant.
            options.AddPolicy(TenantKey, policy => policy
                .AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser()
                .RequireAssertion(ctx => HasPurpose(ctx.User, ApiKeyPurpose.Tenant) && !IsBlocked(ctx.User)));

            // A management key needs the current capability; only roots may have children (one level).
            options.AddPolicy(ManagementKey, policy => policy
                .AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser()
                .RequireAssertion(ctx => HasPurpose(ctx.User, ApiKeyPurpose.Management)
                    && !IsBlocked(ctx.User)
                    && Flag(ctx.User, CanCreateSubtenants)
                    && Flag(ctx.User, IsRoot)));
        }
    }
}
