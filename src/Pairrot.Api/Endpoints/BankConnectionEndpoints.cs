using System.Security.Claims;
using Pairrot.Api.Auth;
using Pairrot.Api.Http;
using Pairrot.Core.Banking;
using Pairrot.Infrastructure.Banking;
using Pairrot.Infrastructure.Payments;
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
        "provider_instance_id", "address_status", "inbound_address", "address_created_at", "provisioning_error", "reception",
        "address_replaced", "created_at", "updated_at",
        "archived_at",
    ];

    private static readonly HashSet<string> UpdateFields = ["label", .. ImmutableFields];

    /// <summary>The receiving account always comes from the saved connection, so account fields are unknown here.</summary>
    private static readonly HashSet<string> PaymentQrFields = ["amount", "currency", "variable_symbol", "message"];

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
        group.MapPost("/{id:guid}/payment-qr", PaymentQrAsync);
        group.MapPost("/{id:guid}/payment-qr.png", PaymentQrPngAsync);
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
            : Results.Created($"{http.Request.Path}/{result.Value!.Connection.Id}", BankConnectionResponse.From(result.Value, banks.ProviderName));
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

    private static async Task<IResult> PaymentQrAsync(
        Guid id, HttpContext http, ClaimsPrincipal user, PaymentQrService qr, CancellationToken ct)
    {
        var (result, error) = await GeneratePaymentQrAsync(id, http, user, qr, ct);
        return result is null ? error! : Results.Ok(PaymentQrResponse.From(result));
    }

    private static async Task<IResult> PaymentQrPngAsync(
        Guid id, HttpContext http, ClaimsPrincipal user, PaymentQrService qr, CancellationToken ct)
    {
        var (result, error) = await GeneratePaymentQrAsync(id, http, user, qr, ct);
        if (result is null)
        {
            return error!;
        }

        var name = result.Payment.VariableSymbol is { } symbol ? $"payment-qr-{symbol}.png" : "payment-qr.png";
        return Results.File(PaymentQrService.RenderPng(result.Payment.Payload), "image/png", name);
    }

    /// <summary>Shared by the JSON and PNG endpoints, so both validate and authorize identically.</summary>
    private static async Task<(PaymentQr? Result, IResult? Error)> GeneratePaymentQrAsync(
        Guid id, HttpContext http, ClaimsPrincipal user, PaymentQrService qr, CancellationToken ct)
    {
        // Payment details are private to the payer and payee; keep them out of shared caches.
        http.Response.Headers.CacheControl = "no-store";

        var (body, error) = await JsonBody.ReadAsync(http.Request, PaymentQrFields);
        if (body is null)
        {
            return (null, error);
        }

        if (!body.TryGetInteger("amount", out var amount, out error)
            || !body.TryGetString("currency", out var currency, out error)
            || !body.TryGetString("variable_symbol", out var variableSymbol, out error)
            || !body.TryGetString("message", out var message, out error))
        {
            return (null, error);
        }

        var input = new PaymentQrInput(amount.Value, currency.Value, variableSymbol.Value, message.Value);
        var result = await qr.GenerateAsync(user.GetTenantId(), Subtenant(http), id, input, ct);
        return result.Error is { } failure ? (null, ApiErrors.From(failure)) : (result.Value, null);
    }

    private static IResult ToResponse(ServiceResult<BankConnectionView> result, BankConnectionService banks) =>
        result.Error is { } failure
            ? ApiErrors.From(failure)
            : Results.Ok(BankConnectionResponse.From(result.Value!, banks.ProviderName));

    /// <summary>The child named in the route, or <c>null</c> for the caller's own tenant.</summary>
    private static Guid? Subtenant(HttpContext http) =>
        http.GetRouteValue(SubtenantRouteValue) is string value && Guid.TryParse(value, out var id) ? id : null;
}
