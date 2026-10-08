using System.Globalization;
using System.Security.Claims;
using Microsoft.Net.Http.Headers;
using Pairrot.Api.Auth;
using Pairrot.Api.Http;
using Pairrot.Core.Inbound;
using Pairrot.Core.Webhooks;
using Pairrot.Infrastructure.Tenants;
using Pairrot.Infrastructure.Webhooks;

namespace Pairrot.Api.Endpoints;

/// <summary>
/// Feature 010's webhook management. Mapped three times:
/// <list type="bullet">
/// <item><c>/v1/tenant</c>: the caller's own endpoint; a parent administrator's session may also
/// subscribe to direct children (an ordinary tenant key may not).</item>
/// <item><c>/v1/management</c>: the same for a management key, e.g. NaLekci's backend.</item>
/// <item><c>/v1/subtenants/{id}</c>: a direct child's own endpoint, never the parent's.</item>
/// </list>
/// </summary>
public static class PaymentWebhookEndpoints
{
    private const string SubtenantRouteValue = "subtenantId";

    private static readonly HashSet<string> CreateFields = ["url", "scope", "environment", "event_types"];
    private static readonly HashSet<string> UpdateFields = ["url", "scope", "state", "event_types"];
    private static readonly HashSet<string> RotateFields = ["expected_version", "grace_period_seconds", "revoke_retiring"];
    private static readonly HashSet<string> DeliveryFields = ["expected_version", "reason"];
    private static readonly HashSet<string> ReplayFields = ["event_ids", "expected_destination_revision", "reason"];

    private static readonly Dictionary<string, WebhookScope> Scopes = Enum.GetValues<WebhookScope>().ToDictionary(InboundMessageResponse.Snake);
    private static readonly Dictionary<string, WebhookEndpointState> States = Enum.GetValues<WebhookEndpointState>().ToDictionary(InboundMessageResponse.Snake);
    private static readonly Dictionary<string, WebhookDeliveryState> DeliveryStates = Enum.GetValues<WebhookDeliveryState>().ToDictionary(InboundMessageResponse.Snake);

    public static void MapTenantWebhooks(this RouteGroupBuilder tenantGroup) => Map(tenantGroup, Authority.Tenant);

    public static void MapSubtenantWebhooks(this RouteGroupBuilder subtenantGroup) =>
        Map(subtenantGroup.MapGroup($"/{{{SubtenantRouteValue}:guid}}"), Authority.Child);

    /// <summary>
    /// <c>/v1/management</c>: the caller's own endpoint with management authority over its
    /// children, and the attention list over them (Feature 016).
    /// </summary>
    public static void MapManagementEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/management")
            .RequireAuthorization(PairrotClaims.Policies.TenantManagement)
            .AddEndpointFilter<TenantContextFilter>();
        Map(group, Authority.Management);
        group.MapAttention(managesChildren: true);
    }

    private enum Authority
    {
        Tenant,
        Management,
        Child,
    }

    private static void Map(RouteGroupBuilder group, Authority authority)
    {
        group.MapPost("/webhook-endpoint", (HttpContext http, ClaimsPrincipal user, WebhookService s, CancellationToken ct) => CreateAsync(http, Caller(http, user, authority), s, ct));
        group.MapGet("/webhook-endpoint", (HttpContext http, ClaimsPrincipal user, WebhookService s, CancellationToken ct) => GetAsync(http, Caller(http, user, authority), s, ct));
        group.MapPatch("/webhook-endpoint", (HttpContext http, ClaimsPrincipal user, WebhookService s, CancellationToken ct) => UpdateAsync(http, Caller(http, user, authority), s, ct));
        group.MapPost("/webhook-endpoint/rotate-secret", (HttpContext http, ClaimsPrincipal user, WebhookService s, CancellationToken ct) => RotateAsync(http, user, Caller(http, user, authority), s, ct));
        group.MapPost("/webhook-endpoint/test", (HttpContext http, ClaimsPrincipal user, WebhookService s, CancellationToken ct) => TestAsync(http, user, Caller(http, user, authority), s, ct));
        group.MapPost("/webhook-endpoint/replay", (HttpContext http, ClaimsPrincipal user, WebhookService s, CancellationToken ct) => ReplayAsync(http, user, Caller(http, user, authority), s, ct));
        group.MapGet("/webhook-events", (HttpContext http, ClaimsPrincipal user, WebhookService s, CancellationToken ct) => EventsAsync(http, Caller(http, user, authority), s, ct));
        group.MapGet("/webhook-deliveries", (HttpContext http, ClaimsPrincipal user, WebhookService s, CancellationToken ct) => DeliveriesAsync(http, Caller(http, user, authority), s, ct));
        group.MapGet("/webhook-deliveries/{id:guid}", async (Guid id, HttpContext http, ClaimsPrincipal user, WebhookService s, CancellationToken ct) =>
        {
            var result = await s.GetDeliveryAsync(Caller(http, user, authority), id, ct);
            return result.Error is { } failure ? ApiErrors.From(failure) : Results.Ok(WebhookDeliveryDetailResponse.From(result.Value!));
        });
        group.MapPost("/webhook-deliveries/{id:guid}/retry", (Guid id, HttpContext http, ClaimsPrincipal user, WebhookService s, CancellationToken ct) =>
            DeliveryCommandAsync(id, http, user, Caller(http, user, authority), "webhook_retry", s.RetryAsync, ct));
        group.MapPost("/webhook-deliveries/{id:guid}/abandon", (Guid id, HttpContext http, ClaimsPrincipal user, WebhookService s, CancellationToken ct) =>
            DeliveryCommandAsync(id, http, user, Caller(http, user, authority), "webhook_abandon", s.AbandonAsync, ct));
    }

    /// <summary>Management authority: the management route, or a parent administrator's session on the tenant route.</summary>
    private static WebhookCaller Caller(HttpContext http, ClaimsPrincipal user, Authority authority) => new(
        user.GetTenantId(),
        authority == Authority.Child ? Subtenant(http) : null,
        authority == Authority.Management
            || (authority == Authority.Tenant && user.IsSession()
                && user.FindFirstValue(PairrotClaims.IsRoot) == bool.TrueString && user.FindFirstValue(PairrotClaims.CanCreateSubtenants) == bool.TrueString),
        user.GetActor());

    private static async Task<IResult> CreateAsync(HttpContext http, WebhookCaller caller, WebhookService webhooks, CancellationToken ct)
    {
        var (body, error) = await JsonBody.ReadAsync(http.Request, CreateFields);
        if (body is null)
        {
            return error!;
        }

        if (!body.TryGetString("url", out var url, out error)
            || !body.TryGetString("scope", out var scopeText, out error)
            || !body.TryGetString("environment", out var environmentText, out error)
            || !body.TryGetStringList("event_types", out var typesValue, out error))
        {
            return error!;
        }

        string[]? eventTypes = null;
        if (typesValue.HasValue)
        {
            if (EventTypesError(typesValue.Value!, out eventTypes) is { } typesError)
            {
                return typesError;
            }
        }

        var scope = WebhookScope.Own;
        if (scopeText.Value is { } scopeValue && !Scopes.TryGetValue(scopeValue, out scope))
        {
            return ApiErrors.Result(StatusCodes.Status400BadRequest, "invalid_scope", "scope must be own or own_and_direct_children.", "scope");
        }

        if (environmentText.Value is not ("live" or "test"))
        {
            return ApiErrors.Result(StatusCodes.Status400BadRequest, "invalid_environment", "environment is required: live or test.", "environment");
        }

        var environment = environmentText.Value == "live" ? InboundEnvironment.Live : InboundEnvironment.Test;
        if (!MatchingEndpoints.TryCommand(http, caller.Actor, "webhook_create", out var command, out error, caller.SubtenantId, url.Value, scopeText.Value,
                environmentText.Value, eventTypes))
        {
            return error!;
        }

        http.Response.Headers.CacheControl = "no-store";
        return MatchingEndpoints.ToResult(http, await webhooks.CreateAsync(caller, url.Value, scope, environment, eventTypes, command!,
            view => WebhookEndpointResponse.From(view), reveal => SignedEndpointResponse.From(reveal), ct));
    }

    private static async Task<IResult> GetAsync(HttpContext http, WebhookCaller caller, WebhookService webhooks, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        var result = await webhooks.GetAsync(caller, ct);
        if (result.Error is { } failure)
        {
            return ApiErrors.From(failure);
        }

        http.Response.Headers.ETag = $"\"{result.Value!.Endpoint.Version.ToString(CultureInfo.InvariantCulture)}\"";
        return Results.Ok(WebhookEndpointResponse.From(result.Value));
    }

    private static async Task<IResult> UpdateAsync(HttpContext http, WebhookCaller caller, WebhookService webhooks, CancellationToken ct)
    {
        var (body, error) = await JsonBody.ReadAsync(http.Request, UpdateFields);
        if (body is null)
        {
            return error!;
        }

        if (!body.TryGetString("url", out var url, out error)
            || !body.TryGetString("scope", out var scopeText, out error)
            || !body.TryGetString("state", out var stateText, out error)
            || !body.TryGetStringList("event_types", out var typesValue, out error))
        {
            return error!;
        }

        var change = new EndpointChange();
        if (typesValue.HasValue)
        {
            if (EventTypesError(typesValue.Value!, out var eventTypes) is { } typesError)
            {
                return typesError;
            }

            change = change with { EventTypes = eventTypes! };
        }
        if (url.HasValue)
        {
            change = change with { Url = url.Value };
        }

        if (scopeText.HasValue)
        {
            if (scopeText.Value is null || !Scopes.TryGetValue(scopeText.Value, out var scope))
            {
                return ApiErrors.Result(StatusCodes.Status400BadRequest, "invalid_scope", "scope must be own or own_and_direct_children.", "scope");
            }

            change = change with { Scope = scope };
        }

        if (stateText.HasValue)
        {
            if (stateText.Value is null || !States.TryGetValue(stateText.Value, out var state))
            {
                return ApiErrors.Result(StatusCodes.Status400BadRequest, "invalid_state", "state must be active or paused.", "state");
            }

            change = change with { State = state };
        }

        var header = http.Request.Headers[HeaderNames.IfMatch].ToString().Trim();
        if (header.Length == 0)
        {
            return ApiErrors.From(ServiceError.PreconditionRequired("Send the endpoint's current version in If-Match (the ETag from GET)."));
        }

        var version = header is ['"', .. var digits, '"'] && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        http.Response.Headers.CacheControl = "no-store";
        var result = await webhooks.UpdateAsync(caller, version, change, ct);
        if (result.Error is { } failure)
        {
            return ApiErrors.From(failure);
        }

        http.Response.Headers.ETag = $"\"{result.Value!.Endpoint.Version.ToString(CultureInfo.InvariantCulture)}\"";
        return Results.Ok(WebhookEndpointResponse.From(result.Value));
    }

    private static async Task<IResult> RotateAsync(HttpContext http, ClaimsPrincipal user, WebhookCaller caller, WebhookService webhooks, CancellationToken ct)
    {
        var (body, error) = await JsonBody.ReadAsync(http.Request, RotateFields);
        if (body is null)
        {
            return error!;
        }

        if (!body.TryGetVersion("expected_version", out var version, out error) || !body.TryGetInteger("grace_period_seconds", out var grace, out error))
        {
            return error!;
        }

        var revoke = body.Has("revoke_retiring") && body.TryGetBoolean("revoke_retiring", out var flag) && flag;
        if (!MatchingEndpoints.TryCommand(http, user.GetActor(), "webhook_rotate", out var command, out error, caller.SubtenantId, version, grace.Value, revoke))
        {
            return error!;
        }

        http.Response.Headers.CacheControl = "no-store";
        return MatchingEndpoints.ToResult(http, await webhooks.RotateAsync(caller, version,
            grace.Value is { } seconds ? TimeSpan.FromSeconds(Math.Clamp(seconds, -1, 10_000_000)) : null, revoke, command!,
            view => WebhookEndpointResponse.From(view), reveal => SignedEndpointResponse.From(reveal), ct));
    }

    private static async Task<IResult> TestAsync(HttpContext http, ClaimsPrincipal user, WebhookCaller caller, WebhookService webhooks, CancellationToken ct)
    {
        if (!MatchingEndpoints.TryCommand(http, user.GetActor(), "webhook_test", out var command, out var error, caller.SubtenantId))
        {
            return error!;
        }

        return MatchingEndpoints.ToResult(http, await webhooks.TestAsync(caller, command!, d => WebhookDeliveryResponse.From(d), ct));
    }

    private static async Task<IResult> ReplayAsync(HttpContext http, ClaimsPrincipal user, WebhookCaller caller, WebhookService webhooks, CancellationToken ct)
    {
        var (body, error) = await JsonBody.ReadAsync(http.Request, ReplayFields);
        if (body is null)
        {
            return error!;
        }

        if (!body.TryGetGuidList("event_ids", out var eventIds, out error)
            || !body.TryGetVersion("expected_destination_revision", out var revision, out error)
            || !body.TryGetString("reason", out var reason, out error))
        {
            return error!;
        }

        if (!MatchingEndpoints.TryCommand(http, user.GetActor(), "webhook_replay", out var command, out error, caller.SubtenantId,
                eventIds.Order().ToList(), revision, reason.Value))
        {
            return error!;
        }

        return MatchingEndpoints.ToResult(http, await webhooks.ReplayAsync(caller, eventIds, revision, reason.Value, command!,
            deliveries => new QueuedDeliveriesResponse(deliveries.Select(WebhookDeliveryResponse.From).ToList()), ct));
    }

    private static async Task<IResult> EventsAsync(HttpContext http, WebhookCaller caller, WebhookService webhooks, CancellationToken ct)
    {
        var query = http.Request.Query;
        if (!Paging.TryRead(query, TenantService.DefaultPageSize, TenantService.MaxPageSize, out var limit, out var cursor, out var error))
        {
            return error!;
        }

        var filter = new EventFilter();
        if (query.TryGetValue("owner_tenant_id", out var ownerValue))
        {
            if (!Guid.TryParse(ownerValue, out var owner))
            {
                return ApiErrors.Validation("owner_tenant_id must be a UUID.");
            }

            filter = filter with { OwnerTenantId = owner };
        }

        if (query.TryGetValue("payment_request_id", out var requestValue))
        {
            if (!Guid.TryParse(requestValue, out var request))
            {
                return ApiErrors.Validation("payment_request_id must be a UUID.");
            }

            filter = filter with { PaymentRequestId = request };
        }

        if (query.TryGetValue("type", out var typeValue))
        {
            if (TypeFilterError(typeValue.ToString()) is { } typeError)
            {
                return typeError;
            }

            filter = filter with { Type = typeValue.ToString() };
        }

        foreach (var name in new[] { "from", "to" })
        {
            if (query.TryGetValue(name, out var value))
            {
                if (!Core.Payments.PaymentRequestRules.TryParseDueAt(value.ToString(), out var time))
                {
                    return ApiErrors.Validation($"{name} must be an ISO 8601 timestamp with an offset.");
                }

                filter = name == "from" ? filter with { From = time } : filter with { To = time };
            }
        }

        var result = await webhooks.ListEventsAsync(caller, filter, limit, cursor, ct);
        return result.Error is { } failure
            ? ApiErrors.From(failure)
            : Results.Ok(new ListResponse<WebhookEventResponse>(result.Value!.Items.Select(WebhookEventResponse.From).ToList(), result.Value.NextCursor));
    }

    private static async Task<IResult> DeliveriesAsync(HttpContext http, WebhookCaller caller, WebhookService webhooks, CancellationToken ct)
    {
        var query = http.Request.Query;
        if (!Paging.TryRead(query, TenantService.DefaultPageSize, TenantService.MaxPageSize, out var limit, out var cursor, out var error))
        {
            return error!;
        }

        WebhookDeliveryState? state = null;
        if (query.TryGetValue("state", out var stateValue))
        {
            if (!DeliveryStates.TryGetValue(stateValue.ToString(), out var parsed))
            {
                return ApiErrors.Validation($"state must be one of: {string.Join(", ", DeliveryStates.Keys)}.");
            }

            state = parsed;
        }

        string? type = null;
        if (query.TryGetValue("type", out var typeValue))
        {
            if (TypeFilterError(typeValue.ToString()) is { } typeError)
            {
                return typeError;
            }

            type = typeValue.ToString();
        }

        var result = await webhooks.ListDeliveriesAsync(caller, state, limit, cursor, ct, type);
        return result.Error is { } failure
            ? ApiErrors.From(failure)
            : Results.Ok(new ListResponse<WebhookDeliveryResponse>(result.Value!.Items.Select(WebhookDeliveryResponse.From).ToList(), result.Value.NextCursor));
    }

    private delegate Task<ServiceResult<Infrastructure.Matching.CommandOutcome>> DeliveryCommand(
        WebhookCaller caller, Guid deliveryId, int? expectedVersion, string? reason, Infrastructure.Matching.CommandRequest command,
        Func<DeliveryView, object> render, CancellationToken ct);

    private static async Task<IResult> DeliveryCommandAsync(
        Guid id, HttpContext http, ClaimsPrincipal user, WebhookCaller caller, string action, DeliveryCommand run, CancellationToken ct)
    {
        var (body, error) = await JsonBody.ReadAsync(http.Request, DeliveryFields);
        if (body is null)
        {
            return error!;
        }

        if (!body.TryGetVersion("expected_version", out var version, out error) || !body.TryGetString("reason", out var reason, out error))
        {
            return error!;
        }

        if (!MatchingEndpoints.TryCommand(http, user.GetActor(), action, out var command, out error, caller.SubtenantId, id, version, reason.Value))
        {
            return error!;
        }

        return MatchingEndpoints.ToResult(http, await run(caller, id, version, reason.Value, command!,
            view => WebhookDeliveryDetailResponse.From(view), ct));
    }

    /// <summary>Feature 016: a subscription list, validated and sorted.</summary>
    private static IResult? EventTypesError(List<string> values, out string[]? types)
    {
        (types, var message) = WebhookEvents.ParseTypes(values);
        return message is null ? null : ApiErrors.Result(StatusCodes.Status400BadRequest, "invalid_event_types", message, "event_types");
    }

    private static IResult? TypeFilterError(string type) =>
        WebhookEvents.Subscribable.Contains(type) || type == WebhookEvents.Test
            ? null
            : ApiErrors.Validation($"type must be one of: {string.Join(", ", WebhookEvents.Subscribable.Append(WebhookEvents.Test))}.");

    private static Guid? Subtenant(HttpContext http) =>
        http.GetRouteValue(SubtenantRouteValue) is string value && Guid.TryParse(value, out var id) ? id : null;
}
