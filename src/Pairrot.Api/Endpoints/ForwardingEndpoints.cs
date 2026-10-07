using System.Security.Claims;
using Pairrot.Api.Auth;
using Pairrot.Api.Http;
using Pairrot.Infrastructure.Inbound;

namespace Pairrot.Api.Endpoints;

/// <summary>
/// Feature 008 routes under a bank connection: forwarding mailboxes, the reception status, and resuming a paused address. Mapped by <see cref="BankConnectionEndpoints"/>
/// for the current tenant and for direct children.
/// </summary>
public static class ForwardingEndpoints
{
    private const string SubtenantRouteValue = "subtenantId";

    private static readonly HashSet<string> RegisterFields = ["address"];

    public static void MapForwarding(this RouteGroupBuilder connections)
    {
        connections.MapGet("/{id:guid}/forwarding-sources", ListAsync);
        connections.MapPost("/{id:guid}/forwarding-sources", RegisterAsync);
        connections.MapDelete("/{id:guid}/forwarding-sources/{sourceId:guid}", RemoveAsync);
        connections.MapGet("/{id:guid}/reception", ReceptionAsync);
        connections.MapPost("/{id:guid}/inbound-address/resume", ResumeAsync);
    }

    private static async Task<IResult> ListAsync(Guid id, HttpContext http, ClaimsPrincipal user, ForwardingService forwarding, CancellationToken ct)
    {
        var result = await forwarding.ListAsync(user.GetTenantId(), Subtenant(http), id, ct);
        return result.Error is { } failure
            ? ApiErrors.From(failure)
            : Results.Ok(new { items = result.Value!.Select(ForwardingSourceResponse.From).ToList() });
    }

    private static async Task<IResult> RegisterAsync(
        Guid id, HttpContext http, ClaimsPrincipal user, ForwardingService forwarding, CancellationToken ct)
    {
        var (body, error) = await JsonBody.ReadAsync(http.Request, RegisterFields);
        if (body is null)
        {
            return error!;
        }

        if (!body.TryGetString("address", out var address, out error))
        {
            return error!;
        }

        var result = await forwarding.RegisterAsync(user.GetTenantId(), Subtenant(http), id, address.Value, user.GetActor(), ct);
        if (result.Error is { } failure)
        {
            return ApiErrors.From(failure);
        }

        var registered = result.Value!;
        var response = ForwardingSourceResponse.From(registered.Source);
        return registered.Created ? Results.Created((string?)null, response) : Results.Ok(response);
    }

    private static async Task<IResult> RemoveAsync(
        Guid id, Guid sourceId, HttpContext http, ClaimsPrincipal user, ForwardingService forwarding, CancellationToken ct)
    {
        var result = await forwarding.RemoveAsync(user.GetTenantId(), Subtenant(http), id, sourceId, user.GetActor(), ct);
        return result.Error is { } failure ? ApiErrors.From(failure) : Results.NoContent();
    }

    private static async Task<IResult> ReceptionAsync(Guid id, HttpContext http, ClaimsPrincipal user, ForwardingService forwarding, CancellationToken ct)
    {
        // Forwarding confirmation codes are private.
        http.Response.Headers.CacheControl = "no-store";
        var result = await forwarding.GetReceptionAsync(user.GetTenantId(), Subtenant(http), id, ct);
        return result.Error is { } failure ? ApiErrors.From(failure) : Results.Ok(ReceptionStatusResponse.From(result.Value!));
    }

    private static async Task<IResult> ResumeAsync(Guid id, HttpContext http, ClaimsPrincipal user, ForwardingService forwarding, CancellationToken ct)
    {
        var result = await forwarding.ResumeAsync(user.GetTenantId(), Subtenant(http), id, user.GetActor(), ct);
        return result.Error is { } failure ? ApiErrors.From(failure) : Results.Ok(ReceptionStatusResponse.From(result.Value!));
    }

    private static Guid? Subtenant(HttpContext http) =>
        http.GetRouteValue(SubtenantRouteValue) is string value && Guid.TryParse(value, out var id) ? id : null;
}
