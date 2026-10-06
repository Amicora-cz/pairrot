using System.Security.Claims;
using Pairrot.Api.Auth;
using Pairrot.Api.Http;
using Pairrot.Infrastructure.Tenants;

namespace Pairrot.Api.Endpoints;

/// <summary>Current-tenant endpoints. They take no tenant identifier: the key decides.</summary>
public static class TenantEndpoints
{
    private static readonly HashSet<string> ConfigurationFields = ["name", "contact_email"];

    public static void MapTenantEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/tenant").RequireAuthorization(PairrotClaims.Policies.TenantKey);

        group.MapGet("", async (ClaimsPrincipal user, TenantService tenants, CancellationToken ct) =>
            await tenants.FindAsync(user.GetTenantId(), ct) is { } tenant
                ? Results.Ok(TenantResponse.From(tenant))
                : ApiErrors.NotFound());

        group.MapGet("/configuration", async (ClaimsPrincipal user, TenantService tenants, CancellationToken ct) =>
            await tenants.FindAsync(user.GetTenantId(), ct) is { } tenant
                ? Results.Ok(ConfigurationResponse.From(tenant))
                : ApiErrors.NotFound());

        group.MapPatch("/configuration", async (
            HttpRequest request, ClaimsPrincipal user, TenantService tenants, CancellationToken ct) =>
        {
            var (body, error) = await JsonBody.ReadAsync(request, ConfigurationFields);
            if (body is null)
            {
                return error!;
            }

            if (!body.TryGetString("name", out var name, out error)
                || !body.TryGetString("contact_email", out var email, out error))
            {
                return error!;
            }

            var result = await tenants.UpdateOwnConfigurationAsync(
                user.GetTenantId(), new TenantChange(name, email), user.GetActor(), ct);

            return result.Error is { } failure
                ? ApiErrors.From(failure)
                : Results.Ok(ConfigurationResponse.From(result.Value!));
        });
    }
}
