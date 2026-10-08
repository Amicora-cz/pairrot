using System.Security.Claims;
using Pairrot.Api.Auth;
using Pairrot.Api.Http;
using Pairrot.Core.Tenants;
using Pairrot.Infrastructure.Erasure;
using Pairrot.Infrastructure.Tenants;

namespace Pairrot.Api.Endpoints;

/// <summary>
/// Management of the caller's direct children, with a management key or a parent administrator's
/// session. Every lookup is scoped to the caller.
/// </summary>
public static class SubtenantEndpoints
{
    private static readonly HashSet<string> CreateFields = ["external_id", "name", "contact_email"];
    private static readonly HashSet<string> UpdateFields = ["name", "contact_email", "status"];
    private static readonly HashSet<string> IssueKeyFields = ["label"];
    private static readonly HashSet<string> EraseFields = ["reason"];

    public static void MapSubtenantEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/subtenants")
            .RequireAuthorization(PairrotClaims.Policies.TenantManagement)
            .AddEndpointFilter<TenantContextFilter>();

        group.MapPost("", CreateAsync);
        group.MapGet("", ListAsync);
        group.MapGet("/{id:guid}", GetAsync);
        group.MapPatch("/{id:guid}", UpdateAsync);
        group.MapDelete("/{id:guid}", DeleteAsync);
        group.MapPost("/{id:guid}/erase", EraseAsync);
        group.MapGet("/{id:guid}/api-keys", ListKeysAsync);
        group.MapPost("/{id:guid}/api-keys", IssueKeyAsync);
        group.MapDelete("/{id:guid}/api-keys/{keyId:guid}", RevokeKeyAsync);
        group.MapGet("/{id:guid}/audit-events", ListAuditEventsAsync).RequireAuthorization(PairrotClaims.Policies.Session);
        group.MapSubtenantBankConnections();
        group.MapSubtenantPaymentRequests();
        group.MapSubtenantInboundMessages();
        group.MapSubtenantTransactions();
        group.MapSubtenantMatching();
        group.MapSubtenantWebhooks();
    }

    private static async Task<IResult> CreateAsync(
        HttpRequest request, ClaimsPrincipal user, TenantService tenants, CancellationToken ct)
    {
        var (body, error) = await JsonBody.ReadAsync(request, CreateFields);
        if (body is null)
        {
            return error!;
        }

        if (!body.TryGetString("external_id", out var externalId, out error)
            || !body.TryGetString("name", out var name, out error)
            || !body.TryGetString("contact_email", out var email, out error))
        {
            return error!;
        }

        var result = await tenants.CreateSubtenantAsync(
            user.GetTenantId(), externalId.Value, name.Value, email.Value, user.GetActor(), ct);
        if (result.Error is { } failure)
        {
            return ApiErrors.From(failure);
        }

        var issued = result.Value!;
        var tenant = issued.Key.Tenant!;
        return Results.Created(
            $"/v1/subtenants/{tenant.Id}",
            new CreatedSubtenantResponse(TenantResponse.From(tenant), IssuedApiKeyResponse.From(issued)));
    }

    private static async Task<IResult> ListAsync(
        HttpRequest request, ClaimsPrincipal user, TenantService tenants, CancellationToken ct)
    {
        var query = request.Query;
        if (!Paging.TryRead(query, TenantService.DefaultPageSize, TenantService.MaxPageSize, out var limit, out var cursor, out var error))
        {
            return error!;
        }

        TenantStatus? status = null;
        if (query.TryGetValue("status", out var statusValue))
        {
            if (!TryParseStatus(statusValue, out var parsed))
            {
                return ApiErrors.Validation("status must be 'active', 'suspended', or 'deleted'.");
            }

            status = parsed;
        }

        string? externalId = query.TryGetValue("external_id", out var externalIdValue) ? externalIdValue.ToString() : null;

        var page = await tenants.ListSubtenantsAsync(user.GetTenantId(), externalId, status, limit, cursor, ct);
        return Results.Ok(new ListResponse<TenantResponse>(page.Items.Select(t => TenantResponse.From(t)).ToList(), page.NextCursor));
    }

    private static async Task<IResult> GetAsync(
        Guid id, ClaimsPrincipal user, TenantService tenants, TenantErasureService erasures, CancellationToken ct) =>
        await tenants.FindSubtenantAsync(user.GetTenantId(), id, ct) is { } child
            ? Results.Ok(TenantResponse.From(child, await erasures.FindAsync(child.Id, ct)))
            : ApiErrors.NotFound();

    /// <summary>
    /// Feature 014: queues the erasure of a deleted child past the minimum age; <c>202</c> with the
    /// child and its erasure. A repeat returns the current state with <c>Idempotent-Replayed: true</c>.
    /// </summary>
    private static async Task<IResult> EraseAsync(
        Guid id, HttpContext http, ClaimsPrincipal user, TenantService tenants, TenantErasureService erasures, CancellationToken ct)
    {
        if (!MatchingEndpoints.TryCommand(http, user, "tenant.erase", out var command, out var error, id))
        {
            return error!;
        }

        var (body, bodyError) = await JsonBody.ReadAsync(http.Request, EraseFields);
        if (body is null)
        {
            return bodyError!;
        }

        if (!body.TryGetString("reason", out var reason, out bodyError))
        {
            return bodyError!;
        }

        var result = await erasures.RequestForSubtenantAsync(
            user.GetTenantId(), id, reason.Value, command!.IdempotencyKey, user.GetActor(), ct);
        if (result.Error is { } failure)
        {
            return ApiErrors.From(failure);
        }

        var tenant = (await tenants.FindSubtenantAsync(user.GetTenantId(), id, ct))!;
        http.Response.Headers["Idempotent-Replayed"] = result.Value!.Replayed ? "true" : "false";
        return Results.Json(TenantResponse.From(tenant, result.Value.Erasure), statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> UpdateAsync(
        Guid id, HttpRequest request, ClaimsPrincipal user, TenantService tenants, CancellationToken ct)
    {
        var (body, error) = await JsonBody.ReadAsync(request, UpdateFields);
        if (body is null)
        {
            return error!;
        }

        if (!body.TryGetString("name", out var name, out error)
            || !body.TryGetString("contact_email", out var email, out error)
            || !body.TryGetString("status", out var statusValue, out error))
        {
            return error!;
        }

        var status = Optional<TenantStatus>.Unset;
        if (statusValue.HasValue)
        {
            if (!TryParseStatus(statusValue.Value, out var parsed) || parsed == TenantStatus.Deleted)
            {
                return ApiErrors.Validation("status must be 'active' or 'suspended'; use DELETE to delete a subtenant.");
            }

            status = parsed;
        }

        var result = await tenants.UpdateSubtenantAsync(
            user.GetTenantId(), id, new TenantChange(name, email, status), user.GetActor(), ct);

        return result.Error is { } failure ? ApiErrors.From(failure) : Results.Ok(TenantResponse.From(result.Value!));
    }

    private static async Task<IResult> DeleteAsync(Guid id, ClaimsPrincipal user, TenantService tenants, CancellationToken ct)
    {
        var result = await tenants.DeleteSubtenantAsync(user.GetTenantId(), id, user.GetActor(), ct);
        return result.Error is { } failure ? ApiErrors.From(failure) : Results.NoContent();
    }

    private static async Task<IResult> ListKeysAsync(Guid id, ClaimsPrincipal user, TenantService tenants, CancellationToken ct)
    {
        var result = await tenants.ListSubtenantKeysAsync(user.GetTenantId(), id, ct);
        return result.Error is { } failure
            ? ApiErrors.From(failure)
            : Results.Ok(new ListResponse<ApiKeyResponse>(result.Value!.Select(ApiKeyResponse.From).ToList(), null));
    }

    private static async Task<IResult> IssueKeyAsync(
        Guid id, HttpRequest request, ClaimsPrincipal user, TenantService tenants, CancellationToken ct)
    {
        var (body, error) = await JsonBody.ReadAsync(request, IssueKeyFields, allowEmpty: true);
        if (body is null)
        {
            return error!;
        }

        if (!body.TryGetString("label", out var label, out error))
        {
            return error!;
        }

        var result = await tenants.IssueSubtenantKeyAsync(user.GetTenantId(), id, label.Value, user.GetActor(), ct);
        return result.Error is { } failure
            ? ApiErrors.From(failure)
            : Results.Created($"/v1/subtenants/{id}/api-keys/{result.Value!.Key.Id}", IssuedApiKeyResponse.From(result.Value));
    }

    private static async Task<IResult> RevokeKeyAsync(
        Guid id, Guid keyId, ClaimsPrincipal user, TenantService tenants, CancellationToken ct)
    {
        var result = await tenants.RevokeSubtenantKeyAsync(user.GetTenantId(), id, keyId, user.GetActor(), ct);
        return result.Error is { } failure ? ApiErrors.From(failure) : Results.NoContent();
    }

    private static async Task<IResult> ListAuditEventsAsync(
        Guid id, HttpRequest request, ClaimsPrincipal user, TenantService tenants, CancellationToken ct)
    {
        if (!Paging.TryRead(request.Query, TenantService.DefaultPageSize, TenantService.MaxPageSize, out var limit, out var cursor, out var error))
        {
            return error!;
        }

        var result = await tenants.ListSubtenantAuditEventsAsync(user.GetTenantId(), id, limit, cursor, ct);
        return result.Error is { } failure
            ? ApiErrors.From(failure)
            : Results.Ok(new ListResponse<AuditEventResponse>(
                result.Value!.Items.Select(AuditEventResponse.From).ToList(), result.Value.NextCursor));
    }

    private static bool TryParseStatus(string? value, out TenantStatus status)
    {
        status = default;
        return value is "active" or "suspended" or "deleted" && Enum.TryParse(value, ignoreCase: true, out status);
    }
}
