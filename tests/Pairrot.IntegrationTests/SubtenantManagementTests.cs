using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Pairrot.Core.Tenants;

namespace Pairrot.IntegrationTests;

[Collection(PairrotCollection.Name)]
public class SubtenantManagementTests(PairrotFixture fx)
{
    [Fact] // AC 2, 3
    public async Task Management_key_creates_lists_reads_and_updates_children()
    {
        var mgmt = fx.Client(fx.RootManagementKey);
        var instructorExt = PairrotFixture.Unique("instructor");

        var created = await mgmt.PostAsJsonAsync("/v1/subtenants", new
        {
            external_id = instructorExt,
            name = "Instructor A",
            contact_email = "a@example.com",
        }).ExpectAsync(HttpStatusCode.Created);

        var tenant = created["tenant"]!;
        Assert.Equal(fx.RootId.ToString(), tenant.Str("parent_tenant_id"));
        Assert.Equal("active", tenant.Str("status"));
        Assert.False(tenant["capabilities"]!["can_create_subtenants"]!.GetValue<bool>());
        Assert.False(tenant["capabilities"]!["can_create_campaigns"]!.GetValue<bool>());
        Assert.Equal("tenant", created["api_key"]!.Str("purpose"));
        var instructorId = tenant.Str("id");
        var instructorKey = created["api_key"]!.Str("secret");

        var studio = await fx.CreateChildAsync(PairrotFixture.Unique("studio"));
        Assert.NotEqual(instructorKey, studio.Key);

        var filtered = await mgmt.GetAsync($"/v1/subtenants?external_id={instructorExt}").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(instructorId, Assert.Single(filtered["items"]!.AsArray())!.Str("id"));

        var read = await mgmt.GetAsync($"/v1/subtenants/{instructorId}").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("a@example.com", read["configuration"]!.Str("contact_email"));
        Assert.DoesNotContain("prt_", read.ToJsonString());

        var updated = await mgmt.PatchJsonAsync($"/v1/subtenants/{instructorId}", new { name = "Instructor B", contact_email = "b@example.com" })
            .ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Instructor B", updated["configuration"]!.Str("name"));

        // The child sees only itself.
        var own = await fx.Client(instructorKey).GetAsync("/v1/tenant").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(instructorId, own.Str("id"));
        Assert.Equal("Instructor B", own["configuration"]!.Str("name"));
    }

    [Fact]
    public async Task List_is_paginated()
    {
        for (var i = 0; i < 3; i++)
        {
            await fx.CreateChildAsync();
        }

        var mgmt = fx.Client(fx.RootManagementKey);
        var seen = new HashSet<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var url = "/v1/subtenants?limit=2" + (cursor is null ? "" : $"&cursor={cursor}");
            var page = await mgmt.GetAsync(url).ExpectAsync(HttpStatusCode.OK);
            foreach (var item in page["items"]!.AsArray())
            {
                Assert.True(seen.Add(item!.Str("id")));
            }

            cursor = page["next_cursor"]?.GetValue<string>();
            pages++;
        }
        while (cursor is not null);

        Assert.True(pages >= 2);
        await mgmt.GetAsync("/v1/subtenants?limit=0").ExpectErrorAsync(HttpStatusCode.BadRequest, "invalid_request");
        await mgmt.GetAsync("/v1/subtenants?status=bogus").ExpectErrorAsync(HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact] // AC 8
    public async Task Duplicate_external_id_conflicts_even_under_concurrency()
    {
        var externalId = PairrotFixture.Unique("race");
        var mgmt = fx.Client(fx.RootManagementKey);

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            mgmt.PostAsJsonAsync("/v1/subtenants", new { external_id = externalId, name = "Race" })));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(7, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        await fx.WithDbAsync(async db =>
        {
            var child = await db.Tenants.Include(t => t.ApiKeys).SingleAsync(t => t.ExternalId == externalId);
            Assert.Single(child.ApiKeys);
        });

        await mgmt.PostAsJsonAsync("/v1/subtenants", new { external_id = externalId, name = "Again" })
            .ExpectErrorAsync(HttpStatusCode.Conflict, "duplicate_external_id");
    }

    [Fact] // AC 8: uniqueness is per parent
    public async Task Same_external_id_is_allowed_under_a_different_parent()
    {
        var other = await fx.SetupRootAsync(PairrotFixture.Unique("other-root"));
        var externalId = PairrotFixture.Unique("shared");

        await fx.CreateChildAsync(externalId);
        await fx.CreateChildAsync(externalId, managementKey: other.ManagementKey!.Secret);
    }

    [Theory] // AC 6, 7
    [InlineData("""{"name": "x"}""")]
    [InlineData("""{"external_id": "", "name": "x"}""")]
    [InlineData("""{"external_id": "e", "name": ""}""")]
    [InlineData("""{"external_id": "e", "name": "x", "contact_email": "nope"}""")]
    [InlineData("""{"external_id": "e", "name": "x", "can_create_subtenants": true}""")]
    [InlineData("""{"external_id": "e", "name": "x", "parent_tenant_id": "00000000-0000-0000-0000-000000000000"}""")]
    public async Task Invalid_create_requests_are_rejected(string json) =>
        await fx.Client(fx.RootManagementKey).PostRawAsync("/v1/subtenants", json)
            .ExpectErrorAsync(HttpStatusCode.BadRequest, "invalid_request");

    [Fact] // AC 9
    public async Task Keys_can_coexist_and_revocation_takes_effect_immediately()
    {
        var child = await fx.CreateChildAsync();
        var mgmt = fx.Client(fx.RootManagementKey);

        var issued = await mgmt.PostAsJsonAsync($"/v1/subtenants/{child.Id}/api-keys", new { label = "rotation" })
            .ExpectAsync(HttpStatusCode.Created);
        var newKey = issued.Str("secret");
        Assert.Equal("tenant", issued.Str("purpose"));

        await fx.Client(child.Key).GetAsync("/v1/tenant").ExpectAsync(HttpStatusCode.OK);
        await fx.Client(newKey).GetAsync("/v1/tenant").ExpectAsync(HttpStatusCode.OK);

        await mgmt.DeleteAsync($"/v1/subtenants/{child.Id}/api-keys/{child.KeyId}").ExpectAsync(HttpStatusCode.NoContent);
        await mgmt.DeleteAsync($"/v1/subtenants/{child.Id}/api-keys/{child.KeyId}").ExpectAsync(HttpStatusCode.NoContent);

        await fx.Client(child.Key).GetAsync("/v1/tenant").ExpectErrorAsync(HttpStatusCode.Unauthorized, "unauthorized");
        await fx.Client(newKey).GetAsync("/v1/tenant").ExpectAsync(HttpStatusCode.OK);

        var keys = await mgmt.GetAsync($"/v1/subtenants/{child.Id}/api-keys").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(2, keys["items"]!.AsArray().Count);
        Assert.DoesNotContain("secret", keys.ToJsonString());
        Assert.DoesNotContain("prt_", keys.ToJsonString());

        // Issuing a key accepts an empty body too.
        await mgmt.PostAsync($"/v1/subtenants/{child.Id}/api-keys", null).ExpectAsync(HttpStatusCode.Created);
    }

    [Fact] // AC 10
    public async Task Suspension_blocks_the_child_until_reactivated_and_keeps_revoked_keys_revoked()
    {
        var child = await fx.CreateChildAsync();
        var mgmt = fx.Client(fx.RootManagementKey);
        var second = (await mgmt.PostAsync($"/v1/subtenants/{child.Id}/api-keys", null).ExpectAsync(HttpStatusCode.Created))
            .Str("secret");
        await mgmt.DeleteAsync($"/v1/subtenants/{child.Id}/api-keys/{child.KeyId}").ExpectAsync(HttpStatusCode.NoContent);

        await mgmt.PatchJsonAsync($"/v1/subtenants/{child.Id}", new { status = "suspended" }).ExpectAsync(HttpStatusCode.OK);
        await fx.Client(second).GetAsync("/v1/tenant").ExpectErrorAsync(HttpStatusCode.Forbidden, "tenant_suspended");

        var inspected = await mgmt.GetAsync($"/v1/subtenants/{child.Id}").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("suspended", inspected.Str("status"));
        // Keys can still be issued for a suspended child.
        await mgmt.PostAsync($"/v1/subtenants/{child.Id}/api-keys", null).ExpectAsync(HttpStatusCode.Created);

        await mgmt.PatchJsonAsync($"/v1/subtenants/{child.Id}", new { status = "active" }).ExpectAsync(HttpStatusCode.OK);
        await fx.Client(second).GetAsync("/v1/tenant").ExpectAsync(HttpStatusCode.OK);
        await fx.Client(child.Key).GetAsync("/v1/tenant").ExpectErrorAsync(HttpStatusCode.Unauthorized, "unauthorized");

        await mgmt.PatchJsonAsync($"/v1/subtenants/{child.Id}", new { status = "deleted" })
            .ExpectErrorAsync(HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact] // AC 10
    public async Task Suspending_the_parent_blocks_its_children()
    {
        var parent = await fx.SetupRootAsync(PairrotFixture.Unique("suspended-parent"));
        var child = await fx.CreateChildAsync(managementKey: parent.ManagementKey!.Secret);

        await fx.SetStatusAsync(parent.Tenant.Id, TenantStatus.Suspended);

        await fx.Client(child.Key).GetAsync("/v1/tenant").ExpectErrorAsync(HttpStatusCode.Forbidden, "tenant_suspended");
        await fx.Client(parent.ManagementKey.Secret).GetAsync("/v1/subtenants").ExpectErrorAsync(HttpStatusCode.Forbidden, "tenant_suspended");

        await fx.SetStatusAsync(parent.Tenant.Id, TenantStatus.Active);
        await fx.Client(child.Key).GetAsync("/v1/tenant").ExpectAsync(HttpStatusCode.OK);
    }

    [Fact] // AC 11
    public async Task Deleting_a_child_revokes_keys_preserves_history_and_reserves_its_external_id()
    {
        var externalId = PairrotFixture.Unique("deleted");
        var child = await fx.CreateChildAsync(externalId);
        var mgmt = fx.Client(fx.RootManagementKey);

        await mgmt.DeleteAsync($"/v1/subtenants/{child.Id}").ExpectAsync(HttpStatusCode.NoContent);
        await mgmt.DeleteAsync($"/v1/subtenants/{child.Id}").ExpectAsync(HttpStatusCode.NoContent);

        await fx.Client(child.Key).GetAsync("/v1/tenant").ExpectErrorAsync(HttpStatusCode.Unauthorized, "unauthorized");

        var read = await mgmt.GetAsync($"/v1/subtenants/{child.Id}").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("deleted", read.Str("status"));
        Assert.NotNull(read["deleted_at"]);

        var keys = await mgmt.GetAsync($"/v1/subtenants/{child.Id}/api-keys").ExpectAsync(HttpStatusCode.OK);
        Assert.All(keys["items"]!.AsArray(), k => Assert.NotNull(k!["revoked_at"]));

        await mgmt.PatchJsonAsync($"/v1/subtenants/{child.Id}", new { status = "active" })
            .ExpectErrorAsync(HttpStatusCode.Conflict, "tenant_deleted");
        await mgmt.PatchJsonAsync($"/v1/subtenants/{child.Id}", new { name = "Zombie" })
            .ExpectErrorAsync(HttpStatusCode.Conflict, "tenant_deleted");
        await mgmt.PostAsync($"/v1/subtenants/{child.Id}/api-keys", null)
            .ExpectErrorAsync(HttpStatusCode.Conflict, "tenant_deleted");
        await mgmt.PostAsJsonAsync("/v1/subtenants", new { external_id = externalId, name = "Again" })
            .ExpectErrorAsync(HttpStatusCode.Conflict, "duplicate_external_id");

        var active = await mgmt.GetAsync($"/v1/subtenants?external_id={externalId}").ExpectAsync(HttpStatusCode.OK);
        Assert.Empty(active["items"]!.AsArray());
        var deleted = await mgmt.GetAsync($"/v1/subtenants?external_id={externalId}&status=deleted").ExpectAsync(HttpStatusCode.OK);
        Assert.Single(deleted["items"]!.AsArray());
    }
}
