using System.Security.Claims;
using Pairrot.Api.Auth;
using Pairrot.Api.Http;
using Pairrot.Core.Banking;
using Pairrot.Infrastructure.Banking;
using Pairrot.Infrastructure.Tenants;

namespace Pairrot.Api.Endpoints;

/// <summary>
/// Bank connections, mapped twice with the same handlers: under <c>/v1/tenant</c> for the
/// caller's own tenant, and under <c>/v1/subtenants/{subtenantId}</c> for a direct child. The
/// owner always comes from the authorized caller (plus the route's child ID), never from the body.
/// </summary>
public static class BankConnectionEndpoints
{
    private const string SubtenantRouteValue = "subtenantId";

    private static readonly HashSet<string> CreateFields = ["account_number", "iban", "currency", "label"];

    /// <summary>Fields that appear in responses but can never be changed through PATCH.</summary>
    private static readonly HashSet<string> ImmutableFields =
    [
        "id", "tenant_id", "account_number", "iban", "currency", "status", "email_provider",
        "address_status", "inbound_address", "address_created_at", "created_at", "updated_at", "archived_at",
    ];

    private static readonly HashSet<string> UpdateFields = ["label", .. ImmutableFields];

    /// <summary>Maps <c>/bank-connections</c> under the current tenant.</summary>
    public static void MapTenantBankConnections(this RouteGroupBuilder tenantGroup) =>
        Map(tenantGroup.MapGroup("/bank-connections"));

    /// <summary>Maps <c>/{subtenantId}/bank-connections</c> under the subtenant group.</summary>
    public static void MapSubtenantBankConnections(this RouteGroupBuilder subtenantGroup) =>
        Map(subtenantGroup.MapGroup($"/{{{SubtenantRouteValue}:guid}}/bank-connections"));

    private static void Map(RouteGroupBuilder group)
    {
        group.MapGet("", ListAsync);
        group.MapPost("", CreateAsync);
        group.MapGet("/{id:guid}", GetAsync);
        group.MapPatch("/{id:guid}", UpdateAsync);
        group.MapDelete("/{id:guid}", ArchiveAsync);
        group.MapPost("/{id:guid}/inbound-address", AssignAddressAsync);
    }

    private static async Task<IResult> ListAsync(
        HttpContext http, ClaimsPrincipal user, BankConnectionService banks, CancellationToken ct)
    {
        var includeArchived = false;
        if (http.Request.Query.TryGetValue("include_archived", out var value) && !bool.TryParse(value, out includeArchived))
        {
            return ApiErrors.Validation("include_archived must be 'true' or 'false'.");
        }

        var result = await banks.ListAsync(user.GetTenantId(), Subtenant(http), includeArchived, ct);
        return result.Error is { } failure
            ? ApiErrors.From(failure)
            : Results.Ok(new ListResponse<BankConnectionResponse>(
                result.Value!.Select(c => BankConnectionResponse.From(c, banks.ProviderName)).ToList(), null));
    }

    private static async Task<IResult> CreateAsync(
        HttpContext http, ClaimsPrincipal user, BankConnectionService banks, CancellationToken ct)
    {
        var (body, error) = await JsonBody.ReadAsync(http.Request, CreateFields);
        if (body is null)
        {
            return error!;
        }

        if (!body.TryGetString("account_number", out var accountNumber, out error)
            || !body.TryGetString("iban", out var iban, out error)
            || !body.TryGetString("currency", out var currency, out error)
            || !body.TryGetString("label", out var label, out error))
        {
            return error!;
        }

        var input = new BankConnectionInput(accountNumber.Value, iban.Value, currency.Value, label.Value);
        var result = await banks.CreateAsync(user.GetTenantId(), Subtenant(http), input, user.GetActor(), ct);
        return result.Error is { } failure
            ? ApiErrors.From(failure)
            : Results.Created($"{http.Request.Path}/{result.Value!.Id}", BankConnectionResponse.From(result.Value, banks.ProviderName));
    }

    private static async Task<IResult> GetAsync(
        Guid id, HttpContext http, ClaimsPrincipal user, BankConnectionService banks, CancellationToken ct) =>
        ToResponse(await banks.GetAsync(user.GetTenantId(), Subtenant(http), id, ct), banks);

    private static async Task<IResult> UpdateAsync(
        Guid id, HttpContext http, ClaimsPrincipal user, BankConnectionService banks, CancellationToken ct)
    {
        var (body, error) = await JsonBody.ReadAsync(http.Request, UpdateFields);
        if (body is null)
        {
            return error!;
        }

        if (ImmutableFields.FirstOrDefault(body.Has) is { } immutable)
        {
            return ApiErrors.Result(StatusCodes.Status400BadRequest, "immutable_field",
                $"{immutable} cannot be changed. To use another account, create a new bank connection and archive this one.",
                immutable);
        }

        if (!body.TryGetString("label", out var label, out error))
        {
            return error!;
        }

        if (!label.HasValue)
        {
            return ToResponse(await banks.GetAsync(user.GetTenantId(), Subtenant(http), id, ct), banks);
        }

        return ToResponse(
            await banks.UpdateLabelAsync(user.GetTenantId(), Subtenant(http), id, label.Value, user.GetActor(), ct), banks);
    }

    private static async Task<IResult> ArchiveAsync(
        Guid id, HttpContext http, ClaimsPrincipal user, BankConnectionService banks, CancellationToken ct)
    {
        var result = await banks.ArchiveAsync(user.GetTenantId(), Subtenant(http), id, user.GetActor(), ct);
        return result.Error is { } failure ? ApiErrors.From(failure) : Results.NoContent();
    }

    private static async Task<IResult> AssignAddressAsync(
        Guid id, HttpContext http, ClaimsPrincipal user, BankConnectionService banks, CancellationToken ct) =>
        ToResponse(await banks.AssignAddressAsync(user.GetTenantId(), Subtenant(http), id, user.GetActor(), ct), banks);

    private static IResult ToResponse(ServiceResult<BankConnection> result, BankConnectionService banks) =>
        result.Error is { } failure
            ? ApiErrors.From(failure)
            : Results.Ok(BankConnectionResponse.From(result.Value!, banks.ProviderName));

    /// <summary>The child named in the route, or <c>null</c> for the caller's own tenant.</summary>
    private static Guid? Subtenant(HttpContext http) =>
        http.GetRouteValue(SubtenantRouteValue) is string value && Guid.TryParse(value, out var id) ? id : null;
}
