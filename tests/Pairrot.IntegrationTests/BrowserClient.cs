using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;

namespace Pairrot.IntegrationTests;

/// <summary>
/// Behaves like the SPA: keeps cookies, sends the antiforgery token and the expected tenant ID,
/// and talks to the API over HTTPS (the session cookie is <c>Secure</c>).
/// </summary>
public sealed class BrowserClient
{
    public static readonly Uri BaseAddress = new("https://localhost");
    public const string SessionCookieName = "__Host-pairrot-session";

    public BrowserClient(WebApplicationFactory<Program> factory, CookieContainer? cookies = null)
    {
        Cookies = cookies ?? new CookieContainer();
        Http = factory.CreateDefaultClient(BaseAddress, new CookieContainerHandler(Cookies));
    }

    public HttpClient Http { get; }
    public CookieContainer Cookies { get; }
    public string? AntiforgeryToken { get; set; }

    /// <summary>The tenant this "tab" believes is selected; sent in X-Pairrot-Tenant-Id.</summary>
    public Guid? TenantId { get; set; }

    public string? SessionCookie => Cookies.GetCookies(BaseAddress)[SessionCookieName]?.Value;

    /// <summary>A different browser holding a copy of this browser's cookies.</summary>
    public BrowserClient Clone(WebApplicationFactory<Program> factory)
    {
        var copy = new CookieContainer();
        foreach (Cookie cookie in Cookies.GetCookies(BaseAddress))
        {
            copy.Add(BaseAddress, new Cookie(cookie.Name, cookie.Value, cookie.Path));
        }

        return new BrowserClient(factory, copy) { AntiforgeryToken = AntiforgeryToken, TenantId = TenantId };
    }

    public async Task RefreshAntiforgeryAsync()
    {
        var body = await Http.GetAsync("/v1/auth/antiforgery").ExpectAsync(HttpStatusCode.OK);
        AntiforgeryToken = body.Str("token");
    }

    public async Task<JsonNode> LoginAsync(string email, string password = PairrotFixture.Password)
    {
        await RefreshAntiforgeryAsync();
        var summary = await PostAsync("/v1/auth/login", new { email, password }).ExpectAsync(HttpStatusCode.OK);
        TenantId = summary["selected_tenant_id"] is { } id ? Guid.Parse(id.GetValue<string>()) : null;
        // The token is bound to the signed-in user, so it is refreshed after login.
        await RefreshAntiforgeryAsync();
        return summary;
    }

    public async Task<JsonNode> SelectTenantAsync(Guid tenantId)
    {
        var summary = await PostAsync("/v1/auth/select-tenant", new { tenant_id = tenantId }).ExpectAsync(HttpStatusCode.OK);
        TenantId = tenantId;
        return summary;
    }

    public Task<HttpResponseMessage> GetAsync(string url, bool background = false) =>
        SendAsync(HttpMethod.Get, url, null, background: background);

    public Task<HttpResponseMessage> PostAsync(string url, object? body = null) => SendAsync(HttpMethod.Post, url, body);

    public Task<HttpResponseMessage> PatchAsync(string url, object body) => SendAsync(HttpMethod.Patch, url, body);

    public Task<HttpResponseMessage> DeleteAsync(string url) => SendAsync(HttpMethod.Delete, url, null);

    public Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string url, object? body, bool antiforgery = true, bool tenantHeader = true, bool background = false)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (antiforgery && AntiforgeryToken is not null)
        {
            request.Headers.Add("X-XSRF-TOKEN", AntiforgeryToken);
        }

        if (tenantHeader && TenantId is { } tenantId)
        {
            request.Headers.Add("X-Pairrot-Tenant-Id", tenantId.ToString());
        }

        if (background)
        {
            request.Headers.Add("X-Pairrot-Background", "true");
        }

        return Http.SendAsync(request);
    }
}
