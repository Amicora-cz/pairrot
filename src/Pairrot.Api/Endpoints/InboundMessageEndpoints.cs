using System.Security.Claims;
using Pairrot.Api.Auth;
using Pairrot.Api.Http;
using Pairrot.Core.Inbound;
using Pairrot.Core.Payments;
using Pairrot.Infrastructure.Inbound;
using Pairrot.Infrastructure.Tenants;

namespace Pairrot.Api.Endpoints;

/// <summary>
/// Received bank-notification emails, mapped like bank connections: under <c>/v1/tenant</c> for
/// the caller's own tenant and under <c>/v1/subtenants/{subtenantId}</c> for a direct child.
/// Only routed messages are visible; quarantined ones exist only in operator tooling.
/// </summary>
public static class InboundMessageEndpoints
{
    private const string SubtenantRouteValue = "subtenantId";

    private static readonly Dictionary<string, InboundContentState> ContentStates = new()
    {
        ["pending"] = InboundContentState.Pending,
        ["available"] = InboundContentState.Available,
        ["failed"] = InboundContentState.Failed,
    };

    private static readonly Dictionary<string, InboundHoldReason> HoldReasons = Enum.GetValues<InboundHoldReason>()
        .ToDictionary(InboundMessageResponse.Snake);

    /// <summary>Maps <c>/inbound-messages</c> under the current tenant.</summary>
    public static void MapTenantInboundMessages(this RouteGroupBuilder tenantGroup) =>
        Map(tenantGroup.MapGroup("/inbound-messages"));

    /// <summary>Maps <c>/{subtenantId}/inbound-messages</c> under the subtenant group.</summary>
    public static void MapSubtenantInboundMessages(this RouteGroupBuilder subtenantGroup) =>
        Map(subtenantGroup.MapGroup($"/{{{SubtenantRouteValue}:guid}}/inbound-messages"));

    private static void Map(RouteGroupBuilder group)
    {
        group.MapGet("", ListAsync);
        group.MapGet("/{id:guid}", GetAsync);
        group.MapGet("/{id:guid}/source", SourceAsync);
        group.MapPost("/{id:guid}/retry-fetch", RetryFetchAsync);
    }

    private static async Task<IResult> ListAsync(
        HttpContext http, ClaimsPrincipal user, InboundMessageService messages, CancellationToken ct)
    {
        var query = http.Request.Query;
        if (!Paging.TryRead(query, TenantService.DefaultPageSize, TenantService.MaxPageSize, out var limit, out var cursor, out var error))
        {
            return error!;
        }

        var filter = new InboundMessageFilter();
        if (query.TryGetValue("bank_connection_id", out var connectionValue))
        {
            if (!Guid.TryParse(connectionValue, out var connectionId))
            {
                return ApiErrors.Validation("bank_connection_id must be a UUID.");
            }

            filter = filter with { BankConnectionId = connectionId };
        }

        if (query.TryGetValue("content_state", out var stateValue))
        {
            if (!ContentStates.TryGetValue(stateValue.ToString(), out var state))
            {
                return ApiErrors.Validation("content_state must be 'pending', 'available', or 'failed'.");
            }

            filter = filter with { ContentState = state };
        }

        if (query.TryGetValue("hold_reason", out var holdValue))
        {
            if (holdValue.ToString() == "none")
            {
                filter = filter with { OnlyWithoutHold = true };
            }
            else if (HoldReasons.TryGetValue(holdValue.ToString(), out var reason))
            {
                filter = filter with { HoldReason = reason };
            }
            else
            {
                return ApiErrors.Validation($"hold_reason must be 'none' or one of: {string.Join(", ", HoldReasons.Keys)}.");
            }
        }

        if (!TryGetTime(query, "received_after", out var after, out error) || !TryGetTime(query, "received_before", out var before, out error))
        {
            return error!;
        }

        filter = filter with { ReceivedAfter = after, ReceivedBefore = before };
        var result = await messages.ListAsync(user.GetTenantId(), Subtenant(http), filter, limit, cursor, ct);
        return result.Error is { } failure
            ? ApiErrors.From(failure)
            : Results.Ok(new ListResponse<InboundMessageResponse>(
                result.Value!.Items.Select(m => InboundMessageResponse.From(m)).ToList(), result.Value.NextCursor));
    }

    private static async Task<IResult> GetAsync(
        Guid id, HttpContext http, ClaimsPrincipal user, InboundMessageService messages, CancellationToken ct)
    {
        var result = await messages.GetAsync(user.GetTenantId(), Subtenant(http), id, ct);
        return result.Error is { } failure
            ? ApiErrors.From(failure)
            : Results.Ok(InboundMessageResponse.From(result.Value!.Message, result.Value.Jobs));
    }

    private static async Task<IResult> SourceAsync(
        Guid id, HttpContext http, ClaimsPrincipal user, InboundMessageService messages, CancellationToken ct)
    {
        // Bank notifications are private; keep them out of every cache.
        http.Response.Headers.CacheControl = "no-store";
        var result = await messages.GetSourceAsync(user.GetTenantId(), Subtenant(http), id, ct);
        return result.Error is { } failure ? ApiErrors.From(failure) : Results.Ok(InboundMessageSourceResponse.From(result.Value!));
    }

    private static async Task<IResult> RetryFetchAsync(
        Guid id, HttpContext http, ClaimsPrincipal user, InboundMessageService messages, CancellationToken ct)
    {
        var result = await messages.RetryFetchAsync(user.GetTenantId(), Subtenant(http), id, user.GetActor(), ct);
        if (result.Error is { } failure)
        {
            return ApiErrors.From(failure);
        }

        var queued = result.Value!;
        return Results.Accepted(null, new QueuedRetrievalResponse(queued.Job.Id, InboundMessageResponse.Snake(queued.Job.State), queued.Created));
    }

    private static bool TryGetTime(IQueryCollection query, string name, out DateTimeOffset? value, out IResult? error)
    {
        value = null;
        error = null;
        if (!query.TryGetValue(name, out var text))
        {
            return true;
        }

        // Same format as due_at; a '+' offset must be URL-encoded as %2B.
        if (!PaymentRequestRules.TryParseDueAt(text.ToString(), out var parsed))
        {
            error = ApiErrors.Validation($"{name} must be an ISO 8601 timestamp with an offset, e.g. 2026-10-15T18:00:00+02:00.");
            return false;
        }

        value = parsed.ToUniversalTime();
        return true;
    }

    /// <summary>The child named in the route, or <c>null</c> for the caller's own tenant.</summary>
    private static Guid? Subtenant(HttpContext http) =>
        http.GetRouteValue(SubtenantRouteValue) is string value && Guid.TryParse(value, out var id) ? id : null;
}
