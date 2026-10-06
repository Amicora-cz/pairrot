using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;

namespace Pairrot.IntegrationTests;

[Collection(PairrotCollection.Name)]
public class IsolationTests(PairrotFixture fx)
{
    [Fact] // AC 4, 5
    public async Task Tenant_keys_cannot_use_management_endpoints()
    {
        var child = await fx.CreateChildAsync();
        var sibling = await fx.CreateChildAsync();

        foreach (var key in new[] { child.Key, fx.RootTenantKey })
        {
            var client = fx.Client(key);
            await client.GetAsync("/v1/subtenants").ExpectErrorAsync(HttpStatusCode.Forbidden, "forbidden");
            await client.GetAsync($"/v1/subtenants/{sibling.Id}").ExpectErrorAsync(HttpStatusCode.Forbidden, "forbidden");
            await client.GetAsync($"/v1/subtenants/{sibling.Id}/api-keys").ExpectErrorAsync(HttpStatusCode.Forbidden, "forbidden");
            await client.PostAsJsonAsync("/v1/subtenants", new { external_id = "grandchild", name = "x" })
                .ExpectErrorAsync(HttpStatusCode.Forbidden, "forbidden");
        }
    }

    [Fact]
    public async Task Management_key_is_not_a_tenant_key() =>
        await fx.Client(fx.RootManagementKey).GetAsync("/v1/tenant/configuration")
            .ExpectErrorAsync(HttpStatusCode.Forbidden, "forbidden");

    [Fact] // AC 4: foreign resources look exactly like unknown ones
    public async Task Unrelated_parent_cannot_see_or_touch_another_parents_children()
    {
        var child = await fx.CreateChildAsync();
        var other = await fx.SetupRootAsync(PairrotFixture.Unique("unrelated"));
        var foreign = fx.Client(other.ManagementKey!.Secret);
        var unknown = Guid.CreateVersion7();

        foreach (var id in new[] { child.Id, unknown })
        {
            await foreign.GetAsync($"/v1/subtenants/{id}").ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");
            await foreign.PatchJsonAsync($"/v1/subtenants/{id}", new { name = "Hijacked" }).ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");
            await foreign.DeleteAsync($"/v1/subtenants/{id}").ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");
            await foreign.GetAsync($"/v1/subtenants/{id}/api-keys").ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");
            await foreign.PostAsync($"/v1/subtenants/{id}/api-keys", null).ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");
            await foreign.DeleteAsync($"/v1/subtenants/{id}/api-keys/{child.KeyId}").ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");
        }

        var list = await foreign.GetAsync("/v1/subtenants").ExpectAsync(HttpStatusCode.OK);
        Assert.Empty(list["items"]!.AsArray());

        // The child is untouched and its key still works.
        var own = await fx.Client(child.Key).GetAsync("/v1/tenant").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Test child", own["configuration"]!.Str("name"));
    }

    [Fact] // AC 4: a key ID from another child cannot be revoked through a different child
    public async Task Key_must_belong_to_the_addressed_child()
    {
        var a = await fx.CreateChildAsync();
        var b = await fx.CreateChildAsync();

        await fx.Client(fx.RootManagementKey).DeleteAsync($"/v1/subtenants/{a.Id}/api-keys/{b.KeyId}")
            .ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");
        await fx.Client(b.Key).GetAsync("/v1/tenant").ExpectAsync(HttpStatusCode.OK);
    }

    [Fact] // AC 5: capability changes apply on the next request
    public async Task Management_key_without_capability_is_forbidden()
    {
        var parent = await fx.SetupRootAsync(PairrotFixture.Unique("no-capability"));
        var mgmt = fx.Client(parent.ManagementKey!.Secret);
        await mgmt.GetAsync("/v1/subtenants").ExpectAsync(HttpStatusCode.OK);

        await fx.WithDbAsync(db => db.Tenants.Where(t => t.Id == parent.Tenant.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.CanCreateSubtenants, false)));

        await mgmt.GetAsync("/v1/subtenants").ExpectErrorAsync(HttpStatusCode.Forbidden, "forbidden");
        await mgmt.PostAsJsonAsync("/v1/subtenants", new { external_id = "x", name = "x" })
            .ExpectErrorAsync(HttpStatusCode.Forbidden, "forbidden");
    }
}
