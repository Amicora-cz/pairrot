using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Pairrot.Api.Http;
using Pairrot.Infrastructure.Users;

namespace Pairrot.Api.Auth;

public static class PairrotHeaders
{
    /// <summary>The tenant the browser expects to act on; a consistency check, never authority.</summary>
    public const string TenantId = "X-Pairrot-Tenant-Id";

    /// <summary>Marks background polling, which must not keep an idle session alive.</summary>
    public const string Background = "X-Pairrot-Background";

    public const string Antiforgery = "X-XSRF-TOKEN";
}

public static class PairrotAuth
{
    /// <summary>Default scheme: an <c>Authorization</c> header selects API keys, otherwise the session cookie.</summary>
    public const string Scheme = "Pairrot";

    public const string SessionScheme = "Session";
    public const string SessionCookieName = "__Host-pairrot-session";
    public const string AntiforgeryCookieName = "__Host-pairrot-antiforgery";

    public static void AddPairrotAuthentication(this IServiceCollection services)
    {
        services
            .AddAuthentication(Scheme)
            .AddPolicyScheme(Scheme, null, options =>
                // No fallback: if a supplied key is invalid, the cookie is not consulted.
                options.ForwardDefaultSelector = context => context.Request.Headers.ContainsKey(Microsoft.Net.Http.Headers.HeaderNames.Authorization)
                    ? ApiKeyAuthenticationHandler.SchemeName
                    : SessionScheme)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, null)
            .AddCookie(SessionScheme, options =>
            {
                options.Cookie.Name = SessionCookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.Path = "/";
                // Expiry is enforced by the session row; the cookie gets the absolute expiry at sign-in.
                options.SlidingExpiration = false;
                options.Events = new CookieAuthenticationEvents
                {
                    OnValidatePrincipal = ValidateSessionAsync,
                    OnRedirectToLogin = context => WriteUnauthorizedAsync(context.HttpContext),
                    OnRedirectToAccessDenied = context => WriteForbiddenAsync(context.HttpContext),
                };
            });

        services.AddAntiforgery(options =>
        {
            options.HeaderName = PairrotHeaders.Antiforgery;
            options.Cookie.Name = AntiforgeryCookieName;
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
        });
    }

    /// <summary>The session validated for this request, if the caller uses a browser session.</summary>
    public static ValidatedSession? GetValidatedSession(this HttpContext context) =>
        context.Items.TryGetValue(typeof(ValidatedSession), out var value) ? value as ValidatedSession : null;

    public static Task WriteUnauthorizedAsync(HttpContext context) =>
        ApiErrors.WriteAsync(context, StatusCodes.Status401Unauthorized, "unauthorized",
            "Sign in, or supply an API key (Authorization: Bearer <key>).");

    public static Task WriteForbiddenAsync(HttpContext context)
    {
        var user = context.User;
        if (user.IsBlocked())
        {
            return ApiErrors.WriteAsync(context, StatusCodes.Status403Forbidden, "tenant_suspended",
                "The tenant or its parent is not active.");
        }

        if (user.IsSession() && user.GetSelectedTenantId() is null)
        {
            return ApiErrors.WriteAsync(context, StatusCodes.Status403Forbidden, "tenant_not_selected",
                "Select a tenant first (POST /v1/auth/select-tenant).");
        }

        return ApiErrors.WriteAsync(context, StatusCodes.Status403Forbidden, "forbidden",
            user.IsSession()
                ? "You are not permitted to perform this operation for the selected tenant."
                : "This API key is not permitted to use this endpoint.");
    }

    /// <summary>
    /// Runs on every cookie-authenticated request: the session row, the user, and the selected
    /// tenant membership are re-read, so revocation and access changes apply immediately.
    /// </summary>
    private static async Task ValidateSessionAsync(CookieValidatePrincipalContext context)
    {
        var http = context.HttpContext;
        var principal = context.Principal;
        if (principal is null
            || !Guid.TryParse(principal.FindFirst(PairrotClaims.UserId)?.Value, out var userId)
            || !Guid.TryParse(principal.FindFirst(PairrotClaims.SessionId)?.Value, out var sessionId))
        {
            context.RejectPrincipal();
            return;
        }

        var background = !string.IsNullOrEmpty(http.Request.Headers[PairrotHeaders.Background]);
        var auth = http.RequestServices.GetRequiredService<AuthService>();
        var session = await auth.ValidateSessionAsync(sessionId, userId, recordActivity: !background, http.RequestAborted);
        if (session is null)
        {
            context.RejectPrincipal();
            await http.SignOutAsync(SessionScheme);
            return;
        }

        http.Items[typeof(ValidatedSession)] = session;
        context.ReplacePrincipal(PairrotClaims.ForSession(session));
        context.ShouldRenew = false;
    }
}
