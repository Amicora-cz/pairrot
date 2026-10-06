using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pairrot.Api.Http;
using Pairrot.Core.ApiKeys;
using Pairrot.Core.Tenants;
using Pairrot.Infrastructure.Persistence;

namespace Pairrot.Api.Auth;

/// <summary>
/// Authenticates <c>Authorization: Bearer prt_...</c> keys against current database state on
/// every request, so revocation, suspension, and capability changes apply immediately.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    PairrotDbContext db)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header))
        {
            return AuthenticateResult.NoResult();
        }

        const string bearer = "Bearer ";
        if (!header.StartsWith(bearer, StringComparison.OrdinalIgnoreCase)
            || !ApiKeySecret.TryParse(header[bearer.Length..].Trim(), out var keyId, out var secret))
        {
            return AuthenticateResult.Fail("Malformed API key.");
        }

        var key = await db.ApiKeys.AsNoTracking()
            .Include(k => k.Tenant).ThenInclude(t => t!.ParentTenant)
            .SingleOrDefaultAsync(k => k.Id == keyId, Context.RequestAborted);

        if (key?.Tenant is null
            || !ApiKeySecret.Verify(secret, key.SecretHash)
            || key.RevokedAt is not null
            || key.Tenant.Status == TenantStatus.Deleted)
        {
            return AuthenticateResult.Fail("Invalid API key.");
        }

        var tenant = key.Tenant;
        var blocked = tenant.Status != TenantStatus.Active
            || (tenant.ParentTenant is { } parent && parent.Status != TenantStatus.Active);

        var identity = new ClaimsIdentity(
        [
            new Claim(PairrotClaims.TenantId, tenant.Id.ToString()),
            new Claim(PairrotClaims.ApiKeyId, key.Id.ToString()),
            new Claim(PairrotClaims.Purpose, key.Purpose.ToString()),
            new Claim(PairrotClaims.IsRoot, tenant.IsRoot.ToString()),
            new Claim(PairrotClaims.CanCreateSubtenants, tenant.CanCreateSubtenants.ToString()),
            new Claim(PairrotClaims.Blocked, blocked.ToString()),
        ], SchemeName);

        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        ApiErrors.WriteAsync(Context, StatusCodes.Status401Unauthorized, "unauthorized",
            "A valid API key is required (Authorization: Bearer <key>).");

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        Context.User.IsBlocked()
            ? ApiErrors.WriteAsync(Context, StatusCodes.Status403Forbidden, "tenant_suspended",
                "The tenant or its parent is not active.")
            : ApiErrors.WriteAsync(Context, StatusCodes.Status403Forbidden, "forbidden",
                "This API key is not permitted to use this endpoint.");
}
