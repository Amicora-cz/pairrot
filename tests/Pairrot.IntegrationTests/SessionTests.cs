using System.Net;
using Microsoft.EntityFrameworkCore;

namespace Pairrot.IntegrationTests;

[Collection(PairrotCollection.Name)]
public class SessionTests(PairrotFixture fx)
{
    [Fact]
    public async Task Login_returns_user_and_memberships_and_selects_the_only_tenant()
    {
        var child = await fx.CreateChildAsync();
        var email = await fx.CreateUserAsync(child.Id);
        var browser = new BrowserClient(fx.Factory);

        var summary = await browser.LoginAsync(email);

        Assert.Equal(email, summary["user"]!.Str("email"));
        Assert.Equal(child.Id.ToString(), summary.Str("selected_tenant_id"));
        var membership = Assert.Single(summary["memberships"]!.AsArray())!;
        Assert.Equal("admin", membership!.Str("role"));
        Assert.Equal(child.Id.ToString(), membership["tenant"]!.Str("id"));
        Assert.NotNull(browser.SessionCookie);

        // Nothing secret in the summary.
        var json = summary.ToJsonString();
        Assert.DoesNotContain(PairrotFixture.Password, json);
        Assert.DoesNotContain(browser.SessionCookie!, json);
    }

    [Fact] // AC 7
    public async Task Page_reload_keeps_the_session()
    {
        var browser = await fx.SignInAsync(fx.RootId);

        // A reload is just another request with the same cookie.
        await browser.GetAsync("/v1/auth/session").ExpectAsync(HttpStatusCode.OK);
        await browser.GetAsync("/v1/tenant").ExpectAsync(HttpStatusCode.OK);
    }

    [Fact] // AC 7
    public async Task Idle_and_absolute_expiry_are_enforced_by_the_server()
    {
        var idle = await fx.SignInAsync(fx.RootId);
        await ExpireAsync(idle, idle: true);
        await idle.GetAsync("/v1/auth/session").ExpectErrorAsync(HttpStatusCode.Unauthorized, "unauthorized");

        var absolute = await fx.SignInAsync(fx.RootId);
        await ExpireAsync(absolute, idle: false);
        await absolute.GetAsync("/v1/auth/session").ExpectErrorAsync(HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Fact] // AC 7
    public async Task Activity_extends_the_idle_expiry_but_background_polling_does_not()
    {
        var browser = await fx.SignInAsync(fx.RootId);
        var userId = await UserIdAsync(browser);
        var past = DateTimeOffset.UtcNow.AddMinutes(-10);
        await fx.WithDbAsync(db => db.Sessions.Where(s => s.UserId == userId)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.LastActivityAt, past)));

        await browser.GetAsync("/v1/auth/session", background: true).ExpectAsync(HttpStatusCode.OK);
        Assert.True(await LastActivityAsync(userId) < DateTimeOffset.UtcNow.AddMinutes(-9));

        await browser.GetAsync("/v1/auth/session").ExpectAsync(HttpStatusCode.OK);
        Assert.True(await LastActivityAsync(userId) > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact] // AC 7, AC 8
    public async Task Logout_revokes_the_session_and_a_copied_cookie_stops_working()
    {
        var browser = await fx.SignInAsync(fx.RootId);
        var stolen = browser.Clone(fx.Factory);
        await stolen.GetAsync("/v1/auth/session").ExpectAsync(HttpStatusCode.OK);

        await browser.PostAsync("/v1/auth/logout").ExpectAsync(HttpStatusCode.NoContent);

        Assert.Null(browser.SessionCookie);
        await browser.GetAsync("/v1/auth/session").ExpectErrorAsync(HttpStatusCode.Unauthorized, "unauthorized");
        await stolen.GetAsync("/v1/auth/session").ExpectErrorAsync(HttpStatusCode.Unauthorized, "unauthorized");
        await stolen.GetAsync("/v1/tenant").ExpectErrorAsync(HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Fact] // AC 7
    public async Task Logout_everywhere_revokes_every_session_of_the_user()
    {
        var email = await fx.CreateUserAsync(fx.RootId);
        var laptop = new BrowserClient(fx.Factory);
        var phone = new BrowserClient(fx.Factory);
        await laptop.LoginAsync(email);
        await phone.LoginAsync(email);

        await laptop.PostAsync("/v1/auth/logout-all").ExpectAsync(HttpStatusCode.NoContent);

        await laptop.GetAsync("/v1/auth/session").ExpectErrorAsync(HttpStatusCode.Unauthorized, "unauthorized");
        await phone.GetAsync("/v1/auth/session").ExpectErrorAsync(HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Fact] // AC 8
    public async Task Password_change_and_reset_revoke_sessions_and_replayed_cookies_fail()
    {
        var email = await fx.CreateUserAsync(fx.RootId);
        var browser = new BrowserClient(fx.Factory);
        await browser.LoginAsync(email);
        var other = new BrowserClient(fx.Factory);
        await other.LoginAsync(email);
        var replay = browser.Clone(fx.Factory);

        await browser.PostAsync("/v1/auth/change-password", new { current_password = "wrong password!!", new_password = "another long password" })
            .ExpectErrorAsync(HttpStatusCode.BadRequest, "invalid_current_password");
        await browser.PostAsync("/v1/auth/change-password", new { current_password = PairrotFixture.Password, new_password = "short" })
            .ExpectErrorAsync(HttpStatusCode.BadRequest, "invalid_request");

        const string changed = "another long password";
        await browser.PostAsync("/v1/auth/change-password", new { current_password = PairrotFixture.Password, new_password = changed })
            .ExpectAsync(HttpStatusCode.NoContent);

        await browser.GetAsync("/v1/auth/session").ExpectErrorAsync(HttpStatusCode.Unauthorized, "unauthorized");
        await other.GetAsync("/v1/auth/session").ExpectErrorAsync(HttpStatusCode.Unauthorized, "unauthorized");
        await replay.GetAsync("/v1/auth/session").ExpectErrorAsync(HttpStatusCode.Unauthorized, "unauthorized");

        // The old password no longer works; the new one does.
        var again = new BrowserClient(fx.Factory);
        await again.RefreshAntiforgeryAsync();
        await again.PostAsync("/v1/auth/login", new { email, password = PairrotFixture.Password })
            .ExpectErrorAsync(HttpStatusCode.Unauthorized, "invalid_credentials");
        await again.LoginAsync(email, changed);

        // Administrative reset revokes that session too.
        var replayAfterReset = again.Clone(fx.Factory);
        var reset = await fx.WithUsersAsync(users => users.ResetPasswordAsync(email, "reset by an operator", "integration-tests"));
        Assert.Null(reset.Error);
        await again.GetAsync("/v1/auth/session").ExpectErrorAsync(HttpStatusCode.Unauthorized, "unauthorized");
        await replayAfterReset.GetAsync("/v1/auth/session").ExpectErrorAsync(HttpStatusCode.Unauthorized, "unauthorized");
        await new BrowserClient(fx.Factory).LoginAsync(email, "reset by an operator");
    }

    [Fact] // AC 8
    public async Task Disabled_user_is_rejected_on_the_next_request_and_cannot_sign_in()
    {
        var email = await fx.CreateUserAsync(fx.RootId);
        var browser = new BrowserClient(fx.Factory);
        await browser.LoginAsync(email);

        var disabled = await fx.WithUsersAsync(users => users.DisableAsync(email, "integration-tests"));
        Assert.True(disabled.Value);

        await browser.GetAsync("/v1/tenant").ExpectErrorAsync(HttpStatusCode.Unauthorized, "unauthorized");
        var fresh = new BrowserClient(fx.Factory);
        await fresh.RefreshAntiforgeryAsync();
        await fresh.PostAsync("/v1/auth/login", new { email, password = PairrotFixture.Password })
            .ExpectErrorAsync(HttpStatusCode.Unauthorized, "invalid_credentials");
    }

    [Fact] // AC 8
    public async Task Membership_removal_takes_effect_immediately()
    {
        var child = await fx.CreateChildAsync();
        var email = await fx.CreateUserAsync(child.Id);
        var browser = new BrowserClient(fx.Factory);
        await browser.LoginAsync(email);
        await browser.GetAsync("/v1/tenant").ExpectAsync(HttpStatusCode.OK);

        var removed = await fx.WithUsersAsync(users => users.RemoveAdminAsync(email, child.Id.ToString(), "integration-tests"));
        Assert.Null(removed.Error);

        await browser.GetAsync("/v1/tenant").ExpectErrorAsync(HttpStatusCode.Forbidden, "forbidden");
        await browser.PatchAsync("/v1/tenant/configuration", new { name = "Hijacked" }).ExpectErrorAsync(HttpStatusCode.Forbidden, "forbidden");
        var summary = await browser.GetAsync("/v1/auth/session").ExpectAsync(HttpStatusCode.OK);
        Assert.Null(summary["selected_tenant_id"]);
        Assert.Empty(summary["memberships"]!.AsArray());
    }

    [Fact] // AC 8
    public async Task Tenant_or_parent_suspension_blocks_the_session_without_waiting_for_expiry()
    {
        var child = await fx.CreateChildAsync();
        var browser = await fx.SignInAsync(child.Id);

        await fx.Client(fx.RootManagementKey).PatchJsonAsync($"/v1/subtenants/{child.Id}", new { status = "suspended" })
            .ExpectAsync(HttpStatusCode.OK);
        await browser.GetAsync("/v1/tenant").ExpectErrorAsync(HttpStatusCode.Forbidden, "tenant_suspended");
        await browser.PostAsync("/v1/tenant/api-keys", new { purpose = "tenant" }).ExpectErrorAsync(HttpStatusCode.Forbidden, "tenant_suspended");

        var summary = await browser.GetAsync("/v1/auth/session").ExpectAsync(HttpStatusCode.OK);
        Assert.True(Assert.Single(summary["memberships"]!.AsArray())!["blocked"]!.GetValue<bool>());

        await fx.Client(fx.RootManagementKey).PatchJsonAsync($"/v1/subtenants/{child.Id}", new { status = "active" })
            .ExpectAsync(HttpStatusCode.OK);
        await browser.GetAsync("/v1/tenant").ExpectAsync(HttpStatusCode.OK);
    }

    [Fact] // AC 10
    public async Task Cookie_authenticated_changes_without_antiforgery_token_fail()
    {
        var browser = await fx.SignInAsync(fx.RootId);
        var name = (await browser.GetAsync("/v1/tenant/configuration").ExpectAsync(HttpStatusCode.OK)).Str("name");

        await browser.SendAsync(HttpMethod.Patch, "/v1/tenant/configuration", new { name = "CSRF" }, antiforgery: false)
            .ExpectErrorAsync(HttpStatusCode.BadRequest, "invalid_antiforgery_token");
        await browser.SendAsync(HttpMethod.Post, "/v1/tenant/api-keys", new { purpose = "tenant" }, antiforgery: false)
            .ExpectErrorAsync(HttpStatusCode.BadRequest, "invalid_antiforgery_token");
        await browser.SendAsync(HttpMethod.Post, "/v1/auth/logout", null, antiforgery: false)
            .ExpectErrorAsync(HttpStatusCode.BadRequest, "invalid_antiforgery_token");

        // A token issued before login (anonymous) is not valid for the signed-in user.
        var anonymous = new BrowserClient(fx.Factory);
        await anonymous.RefreshAntiforgeryAsync();
        browser.AntiforgeryToken = anonymous.AntiforgeryToken;
        await browser.PatchAsync("/v1/tenant/configuration", new { name = "CSRF" })
            .ExpectErrorAsync(HttpStatusCode.BadRequest, "invalid_antiforgery_token");

        // Nothing changed, and the session is still alive.
        await browser.RefreshAntiforgeryAsync();
        Assert.Equal(name, (await browser.GetAsync("/v1/tenant/configuration").ExpectAsync(HttpStatusCode.OK)).Str("name"));
    }

    [Fact] // AC 10
    public async Task Login_requires_an_antiforgery_token()
    {
        var email = await fx.CreateUserAsync(fx.RootId);
        var browser = new BrowserClient(fx.Factory);
        await browser.PostAsync("/v1/auth/login", new { email, password = PairrotFixture.Password })
            .ExpectErrorAsync(HttpStatusCode.BadRequest, "invalid_antiforgery_token");
        Assert.Null(browser.SessionCookie);
    }

    [Fact] // AC 10
    public async Task Login_failures_are_generic_and_repeated_failures_lock_the_account()
    {
        var email = await fx.CreateUserAsync(fx.RootId);
        var browser = new BrowserClient(fx.Factory);
        await browser.RefreshAntiforgeryAsync();

        var unknown = await (await browser.PostAsync("/v1/auth/login", new { email = "nobody@example.com", password = "whatever password" }))
            .Content.ReadAsStringAsync();
        var wrong = await (await browser.PostAsync("/v1/auth/login", new { email, password = "wrong password" }))
            .Content.ReadAsStringAsync();
        Assert.Equal(unknown, wrong);

        // One failure above, four more reach the lockout threshold (5).
        for (var i = 0; i < 4; i++)
        {
            await browser.PostAsync("/v1/auth/login", new { email, password = "wrong password" })
                .ExpectErrorAsync(HttpStatusCode.Unauthorized, "invalid_credentials");
        }

        // Even the correct password is refused while locked out.
        await browser.PostAsync("/v1/auth/login", new { email, password = PairrotFixture.Password })
            .ExpectErrorAsync(HttpStatusCode.Unauthorized, "invalid_credentials");

        // An administrative reset clears the lockout.
        await fx.WithUsersAsync(users => users.ResetPasswordAsync(email, PairrotFixture.Password, "integration-tests"));
        await browser.LoginAsync(email);
    }

    [Fact] // AC 10
    public async Task Repeated_login_attempts_are_throttled_per_client()
    {
        await using var factory = fx.CreateFactory(loginRateLimitPermits: 2);
        var browser = new BrowserClient(factory);
        await browser.RefreshAntiforgeryAsync();

        for (var i = 0; i < 2; i++)
        {
            await browser.PostAsync("/v1/auth/login", new { email = "nobody@example.com", password = "whatever password" })
                .ExpectErrorAsync(HttpStatusCode.Unauthorized, "invalid_credentials");
        }

        await browser.PostAsync("/v1/auth/login", new { email = "nobody@example.com", password = "whatever password" })
            .ExpectErrorAsync(HttpStatusCode.TooManyRequests, "too_many_requests");
    }

    [Fact]
    public async Task A_new_login_replaces_the_previous_session_of_the_browser()
    {
        var email = await fx.CreateUserAsync(fx.RootId);
        var browser = new BrowserClient(fx.Factory);
        await browser.LoginAsync(email);
        var before = browser.Clone(fx.Factory);

        await browser.LoginAsync(email);

        Assert.NotEqual(before.SessionCookie, browser.SessionCookie);
        await before.GetAsync("/v1/auth/session").ExpectErrorAsync(HttpStatusCode.Unauthorized, "unauthorized");
        await browser.GetAsync("/v1/auth/session").ExpectAsync(HttpStatusCode.OK);
    }

    [Fact] // AC 13
    public async Task Sessions_survive_an_application_restart()
    {
        var browser = await fx.SignInAsync(fx.RootId);

        await using var restarted = fx.CreateFactory();
        var after = browser.Clone(restarted);

        await after.GetAsync("/v1/tenant").ExpectAsync(HttpStatusCode.OK);
        await after.PatchAsync("/v1/tenant/configuration", new { contact_email = "restart@example.com" }).ExpectAsync(HttpStatusCode.OK);
    }

    private async Task ExpireAsync(BrowserClient browser, bool idle)
    {
        var userId = await UserIdAsync(browser);
        var past = DateTimeOffset.UtcNow.AddSeconds(-1);
        await fx.WithDbAsync(db => idle
            ? db.Sessions.Where(s => s.UserId == userId).ExecuteUpdateAsync(u => u.SetProperty(s => s.IdleExpiresAt, past))
            : db.Sessions.Where(s => s.UserId == userId).ExecuteUpdateAsync(u => u
                .SetProperty(s => s.IdleExpiresAt, past)
                .SetProperty(s => s.AbsoluteExpiresAt, past)));
    }

    private static async Task<Guid> UserIdAsync(BrowserClient browser) =>
        Guid.Parse((await browser.GetAsync("/v1/auth/session").ExpectAsync(HttpStatusCode.OK))["user"]!.Str("id"));

    private async Task<DateTimeOffset> LastActivityAsync(Guid userId)
    {
        var result = DateTimeOffset.MinValue;
        await fx.WithDbAsync(async db => result = await db.Sessions.Where(s => s.UserId == userId).Select(s => s.LastActivityAt).SingleAsync());
        return result;
    }
}
