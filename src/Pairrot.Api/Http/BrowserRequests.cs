using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Net.Http.Headers;
using Pairrot.Api.Auth;

namespace Pairrot.Api.Http;

/// <summary>Marks endpoints whose state-changing requests always need an antiforgery token (login, logout, ...).</summary>
public sealed class RequiresAntiforgeryMetadata;

public static class BrowserRequests
{
    /// <summary>
    /// Cookie-authenticated state changes must carry a valid antiforgery token in
    /// <c>X-XSRF-TOKEN</c>; <c>SameSite</c> alone is not relied on. API-key requests are not
    /// cookie-based and are not affected.
    /// </summary>
    public static IApplicationBuilder UseBrowserAntiforgery(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var request = context.Request;
            var unsafeMethod = !(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)
                || HttpMethods.IsOptions(request.Method) || HttpMethods.IsTrace(request.Method));

            var required = unsafeMethod
                && (context.GetEndpoint()?.Metadata.GetMetadata<RequiresAntiforgeryMetadata>() is not null
                    || (!request.Headers.ContainsKey(HeaderNames.Authorization) && context.User.IsSession()));

            if (required && !await context.RequestServices.GetRequiredService<IAntiforgery>().IsRequestValidAsync(context))
            {
                await ApiErrors.WriteAsync(context, StatusCodes.Status400BadRequest, "invalid_antiforgery_token",
                    "A valid antiforgery token is required (GET /v1/auth/antiforgery, header X-XSRF-TOKEN).");
                return;
            }

            await next();
        });
}

/// <summary>
/// For browser sessions, the request must name the tenant the page was showing. A stale tab
/// whose session has since switched tenants gets <c>409 tenant_context_changed</c> instead of
/// changing the newly selected tenant. API-key requests are not affected.
/// </summary>
public sealed class TenantContextFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (http.User.IsSession())
        {
            if (!Guid.TryParse(http.Request.Headers[PairrotHeaders.TenantId], out var expected))
            {
                return ApiErrors.Validation($"Browser requests must send the selected tenant ID in {PairrotHeaders.TenantId}.");
            }

            if (http.User.GetSelectedTenantId() != expected)
            {
                return ApiErrors.Result(StatusCodes.Status409Conflict, "tenant_context_changed",
                    "The selected tenant has changed; reload the session and try again.");
            }
        }

        return await next(context);
    }
}

public static class Paging
{
    public static bool TryRead(IQueryCollection query, int defaultLimit, int maxLimit, out int limit, out Guid? cursor, out IResult? error)
    {
        limit = defaultLimit;
        cursor = null;
        error = null;

        if (query.TryGetValue("limit", out var limitValue)
            && (!int.TryParse(limitValue, out limit) || limit < 1 || limit > maxLimit))
        {
            error = ApiErrors.Validation($"limit must be an integer between 1 and {maxLimit}.");
            return false;
        }

        if (query.TryGetValue("cursor", out var cursorValue))
        {
            if (!Guid.TryParse(cursorValue, out var parsed))
            {
                error = ApiErrors.Validation("cursor is invalid.");
                return false;
            }

            cursor = parsed;
        }

        return true;
    }
}
