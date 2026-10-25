using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Pairrot.Api.Auth;
using Pairrot.Api.Http;
using Pairrot.Core.Payments;
using Pairrot.Infrastructure.Sandbox;
using Pairrot.Infrastructure.Tenants;

namespace Pairrot.Api.Endpoints;

/// <summary>
/// Feature 021: simulated incoming payments. Mapped only in sandbox mode, under the tenant and
/// subtenant groups with the authorization of the groups themselves (like bank connections), so
/// without sandbox mode every one of these routes is an unknown route.
/// </summary>
public static class SandboxEndpoints
{
    private const string SubtenantRouteValue = "subtenantId";
    private const string IdempotencyKeyHeader = "Idempotency-Key";

    private static readonly HashSet<string> IncomingFields =
        ["amount", "variable_symbol", "message", "payer_name", "payer_account", "booked_at", "bank_reference", "direction"];

    private static readonly HashSet<string> PayFields = ["amount", "variable_symbol", "booked_at", "payer_name"];

    /// <summary>Whether sandbox mode is on; read once when routes are mapped.</summary>
    public static bool IsEnabled(this IEndpointRouteBuilder app) =>
        app.ServiceProvider.GetRequiredService<IOptions<SandboxOptions>>().Value.Enabled;

    /// <summary>Maps <c>GET /v1/sandbox</c>, the status the local SPA and integrators probe.</summary>
    public static void MapSandboxStatus(this IEndpointRouteBuilder app)
    {
        if (app.IsEnabled())
        {
            app.MapGet("/v1/sandbox", (SandboxService sandbox) => Results.Ok(new SandboxStatusResponse(true, sandbox.ProfileNames)));
        }
    }

    /// <summary>Maps the sandbox routes of bank connections and payment requests under the current tenant.</summary>
    public static void MapTenantSandbox(this RouteGroupBuilder tenantGroup) => Map(tenantGroup, "");

    /// <summary>Maps them under <c>/{subtenantId}</c> of the subtenant group.</summary>
    public static void MapSubtenantSandbox(this RouteGroupBuilder subtenantGroup) => Map(subtenantGroup, $"/{{{SubtenantRouteValue}:guid}}");

    private static void Map(RouteGroupBuilder group, string prefix)
    {
        group.MapPost($"{prefix}/bank-connections/{{id:guid}}/sandbox/incoming-payments", IncomingAsync);
        group.MapPost($"{prefix}/payment-requests/{{id:guid}}/sandbox/pay", PayAsync);
    }

    private static async Task<IResult> IncomingAsync(
        Guid id, HttpContext http, ClaimsPrincipal user, SandboxService sandbox, CancellationToken ct)
    {
        var (body, error) = await JsonBody.ReadAsync(http.Request, IncomingFields);
        if (body is null)
        {
            return error!;
        }

        if (!TryRead(body, http, out var input, out error))
        {
            return error!;
        }

        return ToResult(await sandbox.SimulateAsync(user.GetTenantId(), Subtenant(http), id, input!, user.GetActor(), ct));
    }

    private static async Task<IResult> PayAsync(
        Guid id, HttpContext http, ClaimsPrincipal user, SandboxService sandbox, CancellationToken ct)
    {
        var (body, error) = await JsonBody.ReadAsync(http.Request, PayFields, allowEmpty: true);
        if (body is null)
        {
            return error!;
        }

        if (!TryRead(body, http, out var input, out error))
        {
            return error!;
        }

        return ToResult(await sandbox.PayAsync(user.GetTenantId(), Subtenant(http), id, input!, user.GetActor(), ct));
    }

    private static bool TryRead(JsonBody body, HttpContext http, out SandboxPaymentInput? input, out IResult? error)
    {
        input = null;
        if (!body.TryGetInteger("amount", out var amount, out error)
            || !body.TryGetString("variable_symbol", out var variableSymbol, out error)
            || !body.TryGetString("message", out var message, out error)
            || !body.TryGetString("payer_name", out var payerName, out error)
            || !body.TryGetString("payer_account", out var payerAccount, out error)
            || !body.TryGetString("booked_at", out var bookedAtText, out error)
            || !body.TryGetString("bank_reference", out var bankReference, out error)
            || !body.TryGetString("direction", out var direction, out error))
        {
            return false;
        }

        DateTimeOffset? bookedAt = null;
        if (bookedAtText.Value is { } text)
        {
            if (!PaymentRequestRules.TryParseDueAt(text, out var parsed))
            {
                error = ApiErrors.Result(StatusCodes.Status400BadRequest, "invalid_booked_at",
                    "booked_at must be an ISO 8601 timestamp with an offset, e.g. 2026-10-25T09:15:00+01:00.", "booked_at");
                return false;
            }

            bookedAt = parsed;
        }

        var key = http.Request.Headers.TryGetValue(IdempotencyKeyHeader, out var header) ? header.ToString().Trim() : null;
        input = new SandboxPaymentInput(amount.Value, variableSymbol.Value, message.Value, payerName.Value, payerAccount.Value,
            bookedAt, bankReference.Value, direction.Value, key);
        return true;
    }

    private static IResult ToResult(ServiceResult<SandboxSimulation> result) =>
        result.Error is { } failure
            ? ApiErrors.From(failure)
            : Results.Accepted(null, SandboxPaymentResponse.From(result.Value!));

    private static Guid? Subtenant(HttpContext http) =>
        http.GetRouteValue(SubtenantRouteValue) is string value && Guid.TryParse(value, out var id) ? id : null;
}

public sealed record SandboxStatusResponse(bool Enabled, IReadOnlyList<string> Profiles);

public sealed record SandboxPaymentResponse(
    Guid InboundMessageId,
    Guid BankConnectionId,
    string Profile,
    string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? PaymentRequestId = null)
{
    public static SandboxPaymentResponse From(SandboxSimulation simulation) =>
        new(simulation.MessageId, simulation.BankConnectionId, simulation.Profile, "accepted", simulation.PaymentRequestId);
}
