using System.Security.Claims;
using Pairrot.Api.Auth;
using Pairrot.Api.Http;
using Pairrot.Core.Tenants;
using Pairrot.Infrastructure.Tenants;

namespace Pairrot.Api.Endpoints;

/// <summary>
/// Current-tenant endpoints. They take no tenant identifier: the API key, or the session's
/// selected tenant, decides.
/// </summary>
public static class TenantEndpoints
{
    private static readonly HashSet<string> ConfigurationFields = ["name", "contact_email"];
    private static readonly HashSet<string> IssueKeyFields = ["purpose", "label"];

    public static void MapTenantEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/tenant")
            .RequireAuthorization(PairrotClaims.Policies.TenantAccess)
            .AddEndpointFilter<TenantContextFilter>();

        group.MapGet("", async (ClaimsPrincipal user, TenantService tenants, CancellationToken ct) =>
            await tenants.FindAsync(user.GetTenantId(), ct) is { } tenant
                ? Results.Ok(TenantResponse.From(tenant))
                : ApiErrors.NotFound());

        group.MapGet("/configuration", async (ClaimsPrincipal user, TenantService tenants, CancellationToken ct) =>
            await tenants.FindAsync(user.GetTenantId(), ct) is { } tenant
                ? Results.Ok(ConfigurationResponse.From(tenant))
                : ApiErrors.NotFound());

        group.MapPatch("/configuration", UpdateConfigurationAsync);
        group.MapTenantBankConnections();

        // Browser sessions only: an ordinary tenant key cannot issue more keys for itself.
        var browser = group.MapGroup("").RequireAuthorization(PairrotClaims.Policies.Session);
        browser.MapGet("/api-keys", ListKeysAsync);
        browser.MapPost("/api-keys", IssueKeyAsync);
        browser.MapDelete("/api-keys/{keyId:guid}", RevokeKeyAsync);
        browser.MapGet("/audit-events", ListAuditEventsAsync);
    }

    private static async Task<IResult> UpdateConfigurationAsync(
        HttpRequest request, ClaimsPrincipal user, TenantService tenants, CancellationToken ct)
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
    }

    private static async Task<IResult> ListKeysAsync(ClaimsPrincipal user, TenantService tenants, CancellationToken ct)
    {
        var keys = await tenants.ListOwnKeysAsync(user.GetTenantId(), ct);
        return Results.Ok(new ListResponse<ApiKeyResponse>(keys.Select(ApiKeyResponse.From).ToList(), null));
    }

    private static async Task<IResult> IssueKeyAsync(
        HttpRequest request, ClaimsPrincipal user, TenantService tenants, CancellationToken ct)
    {
        var (body, error) = await JsonBody.ReadAsync(request, IssueKeyFields);
        if (body is null)
        {
            return error!;
        }

        if (!body.TryGetString("purpose", out var purposeValue, out error)
            || !body.TryGetString("label", out var label, out error))
        {
            return error!;
        }

        ApiKeyPurpose purpose;
        switch (purposeValue.Value)
        {
            case "tenant":
                purpose = ApiKeyPurpose.Tenant;
                break;
            case "management":
                purpose = ApiKeyPurpose.Management;
                break;
            default:
                return ApiErrors.Validation("purpose must be 'tenant' or 'management'.");
        }

        var result = await tenants.IssueOwnKeyAsync(user.GetTenantId(), purpose, label.Value, user.GetActor(), ct);
        return result.Error is { } failure
            ? ApiErrors.From(failure)
            : Results.Created($"/v1/tenant/api-keys/{result.Value!.Key.Id}", IssuedApiKeyResponse.From(result.Value));
    }

    private static async Task<IResult> RevokeKeyAsync(Guid keyId, ClaimsPrincipal user, TenantService tenants, CancellationToken ct)
    {
        var result = await tenants.RevokeOwnKeyAsync(user.GetTenantId(), keyId, user.GetActor(), ct);
        return result.Error is { } failure ? ApiErrors.From(failure) : Results.NoContent();
    }

    private static async Task<IResult> ListAuditEventsAsync(
        HttpRequest request, ClaimsPrincipal user, TenantService tenants, CancellationToken ct)
    {
        if (!Paging.TryRead(request.Query, TenantService.DefaultPageSize, TenantService.MaxPageSize, out var limit, out var cursor, out var error))
        {
            return error!;
        }

        var page = await tenants.ListAuditEventsAsync(user.GetTenantId(), limit, cursor, ct);
        return Results.Ok(new ListResponse<AuditEventResponse>(page.Items.Select(AuditEventResponse.From).ToList(), page.NextCursor));
    }
}
