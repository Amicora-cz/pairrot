using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Pairrot.Api.Auth;
using Pairrot.Api.Http;
using Pairrot.Infrastructure.Users;

namespace Pairrot.Api.Endpoints;

/// <summary>Browser sign-in and session endpoints. They use the session cookie, never API keys.</summary>
public static class AuthEndpoints
{
    public const string LoginRateLimitPolicy = "login";

    private static readonly HashSet<string> LoginFields = ["email", "password"];
    private static readonly HashSet<string> SelectTenantFields = ["tenant_id"];
    private static readonly HashSet<string> ChangePasswordFields = ["current_password", "new_password"];

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/auth").WithMetadata(new RequiresAntiforgeryMetadata());

        group.MapGet("/antiforgery", (HttpContext http, IAntiforgery antiforgery) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(http);
            return Results.Ok(new AntiforgeryResponse(tokens.RequestToken!, tokens.HeaderName!));
        }).AllowAnonymous();

        group.MapPost("/login", LoginAsync).AllowAnonymous().RequireRateLimiting(LoginRateLimitPolicy);

        var session = group.MapGroup("").RequireAuthorization(PairrotClaims.Policies.Session);
        session.MapGet("/session", GetSessionAsync);
        session.MapPost("/select-tenant", SelectTenantAsync);
        session.MapPost("/logout", LogoutAsync);
        session.MapPost("/logout-all", LogoutAllAsync);
        session.MapPost("/change-password", ChangePasswordAsync);
    }

    private static async Task<IResult> LoginAsync(HttpContext http, AuthService auth, CancellationToken ct)
    {
        var (body, error) = await JsonBody.ReadAsync(http.Request, LoginFields);
        if (body is null)
        {
            return error!;
        }

        if (!body.TryGetString("email", out var email, out error) || !body.TryGetString("password", out var password, out error))
        {
            return error!;
        }

        if (string.IsNullOrWhiteSpace(email.Value) || string.IsNullOrEmpty(password.Value))
        {
            return ApiErrors.Validation("email and password are required.");
        }

        Guid? previous = http.User.IsSession() ? http.User.GetSessionId() : null;
        var result = await auth.LoginAsync(email.Value, password.Value, previous, ct);
        if (result.Error is { } failure)
        {
            return ApiErrors.From(failure);
        }

        var created = result.Value!;
        await http.SignInAsync(
            PairrotAuth.SessionScheme,
            PairrotClaims.SessionTicket(created.UserId, created.Id),
            new AuthenticationProperties { IsPersistent = false, AllowRefresh = false, ExpiresUtc = created.AbsoluteExpiresAt });

        var validated = await auth.ValidateSessionAsync(created.Id, created.UserId, recordActivity: false, ct);
        return Results.Ok(SessionSummaryResponse.From(await auth.GetSummaryAsync(validated!, ct)));
    }

    private static async Task<IResult> GetSessionAsync(HttpContext http, AuthService auth, CancellationToken ct) =>
        Results.Ok(SessionSummaryResponse.From(await auth.GetSummaryAsync(http.GetValidatedSession()!, ct)));

    private static async Task<IResult> SelectTenantAsync(HttpContext http, AuthService auth, CancellationToken ct)
    {
        var (body, error) = await JsonBody.ReadAsync(http.Request, SelectTenantFields);
        if (body is null)
        {
            return error!;
        }

        if (!body.TryGetString("tenant_id", out var value, out error))
        {
            return error!;
        }

        if (!Guid.TryParse(value.Value, out var tenantId))
        {
            return ApiErrors.Validation("tenant_id must be a tenant ID.");
        }

        var user = http.User;
        var result = await auth.SelectTenantAsync(user.GetSessionId(), user.GetUserId(), tenantId, ct);
        if (result.Error is { } failure)
        {
            return ApiErrors.From(failure);
        }

        var validated = await auth.ValidateSessionAsync(user.GetSessionId(), user.GetUserId(), recordActivity: false, ct);
        return validated is null
            ? ApiErrors.Result(StatusCodes.Status401Unauthorized, "unauthorized", "The session has ended.")
            : Results.Ok(SessionSummaryResponse.From(await auth.GetSummaryAsync(validated, ct)));
    }

    private static async Task<IResult> LogoutAsync(HttpContext http, ClaimsPrincipal user, AuthService auth, CancellationToken ct)
    {
        await auth.LogoutAsync(user.GetSessionId(), ct);
        await http.SignOutAsync(PairrotAuth.SessionScheme);
        return Results.NoContent();
    }

    private static async Task<IResult> LogoutAllAsync(HttpContext http, ClaimsPrincipal user, AuthService auth, CancellationToken ct)
    {
        await auth.LogoutEverywhereAsync(user.GetUserId(), ct);
        await http.SignOutAsync(PairrotAuth.SessionScheme);
        return Results.NoContent();
    }

    private static async Task<IResult> ChangePasswordAsync(HttpContext http, ClaimsPrincipal user, AuthService auth, CancellationToken ct)
    {
        var (body, error) = await JsonBody.ReadAsync(http.Request, ChangePasswordFields);
        if (body is null)
        {
            return error!;
        }

        if (!body.TryGetString("current_password", out var current, out error)
            || !body.TryGetString("new_password", out var next, out error))
        {
            return error!;
        }

        if (string.IsNullOrEmpty(current.Value) || string.IsNullOrEmpty(next.Value))
        {
            return ApiErrors.Validation("current_password and new_password are required.");
        }

        var result = await auth.ChangePasswordAsync(user.GetUserId(), current.Value, next.Value, ct);
        if (result.Error is { } failure)
        {
            return ApiErrors.From(failure);
        }

        // Every session, including this one, was revoked; the user signs in again.
        await http.SignOutAsync(PairrotAuth.SessionScheme);
        return Results.NoContent();
    }
}
