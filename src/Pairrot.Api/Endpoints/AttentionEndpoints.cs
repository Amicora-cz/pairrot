using System.Security.Claims;
using System.Text.Json;
using Pairrot.Api.Auth;
using Pairrot.Api.Http;
using Pairrot.Infrastructure.Webhooks;

namespace Pairrot.Api.Endpoints;

/// <summary>
/// Feature 016's attention list. <c>/v1/management/attention</c> (a management key or a parent
/// administrator) covers the caller and its direct children; <c>/v1/tenant/attention</c> only the
/// caller's own items.
/// </summary>
public static class AttentionEndpoints
{
    public static void MapAttention(this RouteGroupBuilder group, bool managesChildren)
    {
        group.MapGet("/attention", (HttpContext http, ClaimsPrincipal user, AttentionService s, CancellationToken ct) =>
            ListAsync(http, user, s, managesChildren, ct));
        group.MapGet("/attention/summary", (HttpContext http, ClaimsPrincipal user, AttentionService s, CancellationToken ct) =>
            SummaryAsync(http, user, s, managesChildren, ct));
    }

    private static async Task<IResult> ListAsync(HttpContext http, ClaimsPrincipal user, AttentionService attention, bool managesChildren, CancellationToken ct)
    {
        var query = http.Request.Query;
        if (ReadFilter(query, out var error) is not { } filter)
        {
            return error!;
        }

        var limit = AttentionService.DefaultLimit;
        if (query.TryGetValue("limit", out var limitValue)
            && (!int.TryParse(limitValue, out limit) || limit < 1 || limit > AttentionService.MaxLimit))
        {
            return ApiErrors.Validation($"limit must be an integer between 1 and {AttentionService.MaxLimit}.");
        }

        AttentionCursor? cursor = null;
        if (query.TryGetValue("cursor", out var cursorValue) && (cursor = AttentionCursor.Decode(cursorValue.ToString())) is null)
        {
            return ApiErrors.Validation("cursor is invalid.");
        }

        http.Response.Headers.CacheControl = "no-store";
        var result = await attention.ListAsync(user.GetTenantId(), managesChildren, filter, limit, cursor, ct);
        return result.Error is { } failure
            ? ApiErrors.From(failure)
            : Results.Ok(new AttentionListResponse(result.Value!.Items.Select(AttentionItemResponse.From).ToList(), result.Value.Next?.Encode()));
    }

    private static async Task<IResult> SummaryAsync(HttpContext http, ClaimsPrincipal user, AttentionService attention, bool managesChildren, CancellationToken ct)
    {
        if (ReadFilter(http.Request.Query, out var error) is not { } filter)
        {
            return error!;
        }

        http.Response.Headers.CacheControl = "no-store";
        var result = await attention.SummaryAsync(user.GetTenantId(), managesChildren, filter, ct);
        if (result.Error is { } failure)
        {
            return ApiErrors.From(failure);
        }

        var summary = result.Value!;
        return Results.Ok(new AttentionSummaryResponse(summary.Total, summary.ByKind,
            summary.ByTenant.Select(t => new TenantAttentionResponse(t.TenantId, t.Total, t.ByKind)).ToList()));
    }

    /// <summary><c>kinds</c> (comma-separated), <c>tenant_id</c>, and <c>since</c>.</summary>
    private static AttentionFilter? ReadFilter(IQueryCollection query, out IResult? error)
    {
        error = null;
        IReadOnlyList<string> kinds = AttentionKinds.Default;
        if (query.TryGetValue("kinds", out var kindsValue))
        {
            var requested = kindsValue.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();
            if (requested.Count == 0 || requested.Any(k => !AttentionKinds.All.Contains(k)))
            {
                error = ApiErrors.Validation($"kinds must be a comma-separated list of: {string.Join(", ", AttentionKinds.All)}.");
                return null;
            }

            kinds = requested;
        }

        Guid? tenantId = null;
        if (query.TryGetValue("tenant_id", out var tenantValue))
        {
            if (!Guid.TryParse(tenantValue, out var parsed))
            {
                error = ApiErrors.Validation("tenant_id must be a UUID.");
                return null;
            }

            tenantId = parsed;
        }

        DateTimeOffset? since = null;
        if (query.TryGetValue("since", out var sinceValue))
        {
            if (!Core.Payments.PaymentRequestRules.TryParseDueAt(sinceValue.ToString(), out var parsed))
            {
                error = ApiErrors.Validation("since must be an ISO 8601 timestamp with an offset.");
                return null;
            }

            since = parsed;
        }

        return new AttentionFilter(kinds, tenantId, since);
    }
}

/// <param name="Summary">Kind-specific facts: amounts, symbols, results, and IDs; never sender data or free text.</param>
public sealed record AttentionItemResponse(string Kind, Guid TenantId, Guid? BankConnectionId, Guid SubjectId, DateTimeOffset Since, JsonElement Summary)
{
    public static AttentionItemResponse From(AttentionItem item) => new(
        item.Kind, item.TenantId, item.BankConnectionId, item.SubjectId, item.Since, JsonDocument.Parse(item.Summary).RootElement.Clone());
}

/// <param name="NextCursor">Pass as <c>cursor</c> for the next page; <c>null</c> on the last one.</param>
public sealed record AttentionListResponse(IReadOnlyList<AttentionItemResponse> Items, string? NextCursor);

public sealed record TenantAttentionResponse(Guid TenantId, int Total, IReadOnlyDictionary<string, int> ByKind);

public sealed record AttentionSummaryResponse(int Total, IReadOnlyDictionary<string, int> ByKind, IReadOnlyList<TenantAttentionResponse> ByTenant);
