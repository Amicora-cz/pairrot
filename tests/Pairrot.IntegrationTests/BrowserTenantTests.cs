using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Pairrot.Core.Tenants;

namespace Pairrot.IntegrationTests;

[Collection(PairrotCollection.Name)]
public class BrowserTenantTests(PairrotFixture fx)
{
    [Fact] // AC 2
    public async Task Root_administrator_signs_in_and_generates_a_management_key_without_an_existing_key()
    {
        var browser = await fx.SignInAsync(fx.RootId);
        Assert.Equal(fx.RootId, browser.TenantId);

        var config = await browser.GetAsync("/v1/tenant/configuration").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("nalekci.cz", config.Str("name"));

        var issued = await browser.PostAsync("/v1/tenant/api-keys", new { purpose = "management", label = "from the browser" })
            .ExpectAsync(HttpStatusCode.Created);
        Assert.Equal("management", issued.Str("purpose"));
        var secret = issued.Str("secret");

        // The new key works on its own, without any session.
        await fx.Client(secret).GetAsync("/v1/subtenants").ExpectAsync(HttpStatusCode.OK);
    }

    [Fact] // AC 3
    public async Task Studio_administrator_cannot_reach_its_parent_or_siblings()
    {
        var studio = await fx.CreateChildAsync();
        var sibling = await fx.CreateChildAsync();
        var browser = await fx.SignInAsync(studio.Id);

        var tenant = await browser.GetAsync("/v1/tenant").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(studio.Id.ToString(), tenant.Str("id"));

        await browser.GetAsync("/v1/subtenants").ExpectErrorAsync(HttpStatusCode.Forbidden, "forbidden");
        await browser.GetAsync($"/v1/subtenants/{sibling.Id}").ExpectErrorAsync(HttpStatusCode.Forbidden, "forbidden");
        await browser.GetAsync($"/v1/subtenants/{sibling.Id}/api-keys").ExpectErrorAsync(HttpStatusCode.Forbidden, "forbidden");
        await browser.PostAsync($"/v1/subtenants/{sibling.Id}/api-keys").ExpectErrorAsync(HttpStatusCode.Forbidden, "forbidden");

        // Selecting the parent or a sibling is out of scope.
        await browser.PostAsync("/v1/auth/select-tenant", new { tenant_id = fx.RootId }).ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");
        await browser.PostAsync("/v1/auth/select-tenant", new { tenant_id = sibling.Id }).ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");

        // The session's own key list contains only the studio's keys.
        var keys = await browser.GetAsync("/v1/tenant/api-keys").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(studio.KeyId.ToString(), Assert.Single(keys["items"]!.AsArray())!.Str("id"));
        await browser.DeleteAsync($"/v1/tenant/api-keys/{sibling.KeyId}").ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");
        await fx.Client(sibling.Key).GetAsync("/v1/tenant").ExpectAsync(HttpStatusCode.OK);
    }

    [Fact] // AC 4
    public async Task Tenant_keys_can_be_issued_but_management_keys_need_the_capability()
    {
        var studio = await fx.CreateChildAsync();
        var browser = await fx.SignInAsync(studio.Id);

        var issued = await browser.PostAsync("/v1/tenant/api-keys", new { purpose = "tenant" }).ExpectAsync(HttpStatusCode.Created);
        await fx.Client(issued.Str("secret")).GetAsync("/v1/tenant").ExpectAsync(HttpStatusCode.OK);

        await browser.PostAsync("/v1/tenant/api-keys", new { purpose = "management" }).ExpectErrorAsync(HttpStatusCode.Forbidden, "forbidden");
        await browser.PostAsync("/v1/tenant/api-keys", new { purpose = "admin" }).ExpectErrorAsync(HttpStatusCode.BadRequest, "invalid_request");
        await browser.PostAsync("/v1/tenant/api-keys", new { label = "no purpose" }).ExpectErrorAsync(HttpStatusCode.BadRequest, "invalid_request");

        // A root without can_create_subtenants cannot issue management keys either.
        var root = await fx.SetupRootAsync(PairrotFixture.Unique("root"));
        await fx.WithDbAsync(db => db.Tenants.Where(t => t.Id == root.Tenant.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(t => t.CanCreateSubtenants, false)));
        var rootBrowser = await fx.SignInAsync(root.Tenant.Id);
        await rootBrowser.PostAsync("/v1/tenant/api-keys", new { purpose = "management" }).ExpectErrorAsync(HttpStatusCode.Forbidden, "forbidden");
        await rootBrowser.GetAsync("/v1/subtenants").ExpectErrorAsync(HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact] // AC 4
    public async Task An_ordinary_tenant_key_cannot_issue_keys_or_use_browser_endpoints()
    {
        var studio = await fx.CreateChildAsync();
        var client = fx.Client(studio.Key);

        await client.PostRawAsync("/v1/tenant/api-keys", """{"purpose": "tenant"}""").ExpectErrorAsync(HttpStatusCode.Forbidden, "forbidden");
        await client.GetAsync("/v1/tenant/api-keys").ExpectErrorAsync(HttpStatusCode.Forbidden, "forbidden");
        await client.GetAsync("/v1/tenant/audit-events").ExpectErrorAsync(HttpStatusCode.Forbidden, "forbidden");
        await client.GetAsync("/v1/auth/session").ExpectErrorAsync(HttpStatusCode.Forbidden, "forbidden");

        // A management key cannot read child history either; that endpoint is for sessions.
        await fx.Client(fx.RootManagementKey).GetAsync($"/v1/subtenants/{studio.Id}/audit-events")
            .ExpectErrorAsync(HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact] // AC 5
    public async Task Multi_tenant_user_selects_explicitly_and_a_stale_tab_cannot_change_the_new_tenant()
    {
        var a = await fx.CreateChildAsync();
        var b = await fx.CreateChildAsync();
        var unrelated = await fx.CreateChildAsync();
        var email = await fx.CreateUserAsync(a.Id, b.Id);
        var tabA = new BrowserClient(fx.Factory);

        var summary = await tabA.LoginAsync(email);
        Assert.Null(summary["selected_tenant_id"]);
        Assert.Equal(2, summary["memberships"]!.AsArray().Count);
        await tabA.GetAsync("/v1/tenant").ExpectErrorAsync(HttpStatusCode.Forbidden, "tenant_not_selected");

        await tabA.PostAsync("/v1/auth/select-tenant", new { tenant_id = unrelated.Id }).ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");
        await tabA.SelectTenantAsync(a.Id);
        await tabA.PatchAsync("/v1/tenant/configuration", new { name = "Studio A" }).ExpectAsync(HttpStatusCode.OK);

        // A second tab of the same browser switches the shared session to B.
        var tabB = tabA.Clone(fx.Factory);
        await tabB.SelectTenantAsync(b.Id);

        // The stale tab still believes A is selected and is refused, without changing B.
        await tabA.PatchAsync("/v1/tenant/configuration", new { name = "Meant for A" })
            .ExpectErrorAsync(HttpStatusCode.Conflict, "tenant_context_changed");
        await tabA.PostAsync("/v1/tenant/api-keys", new { purpose = "tenant" })
            .ExpectErrorAsync(HttpStatusCode.Conflict, "tenant_context_changed");
        var bConfig = await tabB.GetAsync("/v1/tenant/configuration").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Test child", bConfig.Str("name"));

        // A missing tenant header is rejected for browser requests.
        await tabB.SendAsync(HttpMethod.Get, "/v1/tenant", null, tenantHeader: false)
            .ExpectErrorAsync(HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact] // AC 6
    public async Task Issued_secret_is_shown_once_and_stored_only_as_a_verifier()
    {
        var browser = await fx.SignInAsync(fx.RootId);
        var issued = await browser.PostAsync("/v1/tenant/api-keys", new { purpose = "tenant", label = "once" })
            .ExpectAsync(HttpStatusCode.Created);
        var secret = issued.Str("secret");
        var keyId = issued.Str("id");

        var list = await browser.GetAsync("/v1/tenant/api-keys").ExpectAsync(HttpStatusCode.OK);
        var listed = list["items"]!.AsArray().Single(k => k!.Str("id") == keyId)!;
        Assert.Null(listed["secret"]);
        Assert.DoesNotContain(secret, list.ToJsonString());

        var history = await browser.GetAsync("/v1/tenant/audit-events").ExpectAsync(HttpStatusCode.OK);
        Assert.DoesNotContain(secret, history.ToJsonString());

        await fx.WithDbAsync(async db =>
        {
            var stored = await db.ApiKeys.SingleAsync(k => k.Id == Guid.Parse(keyId));
            Assert.Equal(32, stored.SecretHash.Length);
            Assert.DoesNotContain(secret, Encoding.UTF8.GetString(stored.SecretHash));
        });

        // The session does not depend on the key: revoking it leaves the session working.
        await browser.DeleteAsync($"/v1/tenant/api-keys/{keyId}").ExpectAsync(HttpStatusCode.NoContent);
        await browser.GetAsync("/v1/tenant").ExpectAsync(HttpStatusCode.OK);
    }

    [Fact] // AC 9
    public async Task Signing_out_and_changing_password_do_not_revoke_integration_keys()
    {
        var studio = await fx.CreateChildAsync();
        var email = await fx.CreateUserAsync(studio.Id);
        var browser = new BrowserClient(fx.Factory);
        await browser.LoginAsync(email);

        await browser.PostAsync("/v1/auth/logout").ExpectAsync(HttpStatusCode.NoContent);
        await fx.Client(studio.Key).GetAsync("/v1/tenant").ExpectAsync(HttpStatusCode.OK);

        await browser.LoginAsync(email);
        await browser.PostAsync("/v1/auth/change-password", new { current_password = PairrotFixture.Password, new_password = "a brand new password" })
            .ExpectAsync(HttpStatusCode.NoContent);
        await fx.Client(studio.Key).GetAsync("/v1/tenant").ExpectAsync(HttpStatusCode.OK);

        await fx.WithUsersAsync(users => users.RemoveAdminAsync(email, studio.Id.ToString(), "integration-tests"));
        await fx.Client(studio.Key).GetAsync("/v1/tenant").ExpectAsync(HttpStatusCode.OK);

        // Explicit revocation still takes effect immediately.
        var admin = await fx.SignInAsync(studio.Id);
        await admin.DeleteAsync($"/v1/tenant/api-keys/{studio.KeyId}").ExpectAsync(HttpStatusCode.NoContent);
        await fx.Client(studio.Key).GetAsync("/v1/tenant").ExpectErrorAsync(HttpStatusCode.Unauthorized, "unauthorized");
        await admin.DeleteAsync($"/v1/tenant/api-keys/{studio.KeyId}").ExpectAsync(HttpStatusCode.NoContent);
    }

    [Fact] // AC 11
    public async Task Contact_email_is_independent_of_logins_and_memberships()
    {
        var studio = await fx.CreateChildAsync();
        var outsider = await fx.CreateUserAsync();
        var browser = await fx.SignInAsync(studio.Id);

        await browser.PatchAsync("/v1/tenant/configuration", new { contact_email = outsider }).ExpectAsync(HttpStatusCode.OK);

        var outsiderBrowser = new BrowserClient(fx.Factory);
        var summary = await outsiderBrowser.LoginAsync(outsider);
        Assert.Empty(summary["memberships"]!.AsArray());
        Assert.Null(summary["selected_tenant_id"]);
        await outsiderBrowser.GetAsync("/v1/tenant").ExpectErrorAsync(HttpStatusCode.Forbidden, "tenant_not_selected");

        // The studio admin's own login is unchanged.
        var me = await browser.GetAsync("/v1/auth/session").ExpectAsync(HttpStatusCode.OK);
        Assert.NotEqual(outsider, me["user"]!.Str("email"));
    }

    [Fact] // AC 12
    public async Task Browser_changes_are_attributed_to_the_user_and_key_changes_to_the_key()
    {
        var studio = await fx.CreateChildAsync();
        var email = await fx.CreateUserAsync(studio.Id);
        var browser = new BrowserClient(fx.Factory);
        var summary = await browser.LoginAsync(email);
        var userId = summary["user"]!.Str("id");

        await browser.PatchAsync("/v1/tenant/configuration", new { name = "Changed in browser" }).ExpectAsync(HttpStatusCode.OK);
        await fx.Client(studio.Key).PatchJsonAsync("/v1/tenant/configuration", new { name = "Changed by key" }).ExpectAsync(HttpStatusCode.OK);

        var history = await browser.GetAsync("/v1/tenant/audit-events").ExpectAsync(HttpStatusCode.OK);
        var updates = history["items"]!.AsArray()
            .Where(e => e!.Str("action") == AuditActions.TenantConfigurationUpdated)
            .ToList();
        Assert.Equal(2, updates.Count);

        // Newest first.
        Assert.Equal("api_key", updates[0]!["actor"]!.Str("type"));
        Assert.Equal(studio.KeyId.ToString(), updates[0]!["actor"]!.Str("api_key_id"));
        Assert.Equal("user", updates[1]!["actor"]!.Str("type"));
        Assert.Equal(userId, updates[1]!["actor"]!.Str("user_id"));
        Assert.Equal(email, updates[1]!["actor"]!.Str("email"));

        await fx.WithDbAsync(async db =>
        {
            var events = await db.AuditEvents.Where(e => e.TargetTenantId == studio.Id && e.Action == AuditActions.TenantConfigurationUpdated)
                .ToListAsync();
            Assert.Contains(events, e => e.ActorUserId == Guid.Parse(userId) && e.ActorApiKeyId == null && e.ActorDescription == null);
            Assert.Contains(events, e => e.ActorApiKeyId == studio.KeyId && e.ActorUserId == null);
        });
    }

    [Fact]
    public async Task Parent_administrator_manages_children_and_reads_their_history_without_becoming_a_member()
    {
        var browser = await fx.SignInAsync(fx.RootId);

        var created = await browser.PostAsync("/v1/subtenants", new { external_id = PairrotFixture.Unique("web"), name = "Created in browser" })
            .ExpectAsync(HttpStatusCode.Created);
        var childId = created["tenant"]!.Str("id");
        var childKey = created["api_key"]!.Str("secret");

        await browser.PatchAsync($"/v1/subtenants/{childId}", new { status = "suspended" }).ExpectAsync(HttpStatusCode.OK);
        await browser.PatchAsync($"/v1/subtenants/{childId}", new { status = "active" }).ExpectAsync(HttpStatusCode.OK);
        await browser.PostAsync($"/v1/subtenants/{childId}/api-keys", new { label = "rotation" }).ExpectAsync(HttpStatusCode.Created);
        await fx.Client(childKey).GetAsync("/v1/tenant").ExpectAsync(HttpStatusCode.OK);

        var history = await browser.GetAsync($"/v1/subtenants/{childId}/audit-events?limit=2").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(2, history["items"]!.AsArray().Count);
        Assert.NotNull(history["next_cursor"]);
        var rest = await browser.GetAsync($"/v1/subtenants/{childId}/audit-events?cursor={history.Str("next_cursor")}")
            .ExpectAsync(HttpStatusCode.OK);
        Assert.Contains(rest["items"]!.AsArray(), e => e!.Str("action") == AuditActions.TenantCreated);
        Assert.All(history["items"]!.AsArray().Concat(rest["items"]!.AsArray()),
            e => Assert.Equal("user", e!["actor"]!.Str("type")));

        // Managing the child does not make the parent administrator a member of it.
        await browser.PostAsync("/v1/auth/select-tenant", new { tenant_id = childId }).ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");

        // An unrelated child's history is out of scope.
        var other = await fx.SetupRootAsync(PairrotFixture.Unique("root"));
        await browser.GetAsync($"/v1/subtenants/{other.Tenant.Id}/audit-events").ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");
    }

    [Fact] // Section 6: no fallback from a failed key to the cookie
    public async Task An_invalid_authorization_header_is_not_rescued_by_a_valid_session_cookie()
    {
        var browser = await fx.SignInAsync(fx.RootId);
        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/tenant");
        request.Headers.Add("Authorization", "Bearer prt_invalid");
        request.Headers.Add("X-Pairrot-Tenant-Id", fx.RootId.ToString());

        await browser.Http.SendAsync(request).ExpectErrorAsync(HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Fact]
    public async Task Unauthenticated_browser_requests_get_json_401_not_redirects()
    {
        var browser = new BrowserClient(fx.Factory);
        await browser.GetAsync("/v1/auth/session").ExpectErrorAsync(HttpStatusCode.Unauthorized, "unauthorized");
        await browser.GetAsync("/v1/tenant/api-keys").ExpectErrorAsync(HttpStatusCode.Unauthorized, "unauthorized");
        await browser.GetAsync("/v1/subtenants").ExpectErrorAsync(HttpStatusCode.Unauthorized, "unauthorized");
    }
}
