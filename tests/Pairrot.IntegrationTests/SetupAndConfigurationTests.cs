using System.Net;
using Microsoft.EntityFrameworkCore;
using Pairrot.Core.Tenants;
using Pairrot.Infrastructure.Tenants;

namespace Pairrot.IntegrationTests;

[Collection(PairrotCollection.Name)]
public class SetupAndConfigurationTests(PairrotFixture fx)
{
    [Fact] // AC 1
    public async Task Setup_creates_root_once_and_repeating_it_changes_nothing()
    {
        var root = await fx.Client(fx.RootTenantKey).GetAsync("/v1/tenant").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("nalekci.cz", root.Str("external_id"));
        Assert.Equal("active", root.Str("status"));
        Assert.Null(root["parent_tenant_id"]);
        Assert.True(root["capabilities"]!["can_create_subtenants"]!.GetValue<bool>());
        Assert.False(root["capabilities"]!["can_create_campaigns"]!.GetValue<bool>());

        var again = await fx.SetupRootAsync(RootTenantAdministration.NaLekciExternalId);
        Assert.False(again.Created);
        Assert.Null(again.TenantKey);
        Assert.Null(again.ManagementKey);

        await fx.WithDbAsync(async db =>
        {
            Assert.Equal(1, await db.Tenants.CountAsync(t => t.ExternalId == "nalekci.cz" && t.ParentTenantId == null));
            Assert.Equal(2, await db.ApiKeys.CountAsync(k => k.TenantId == fx.RootId && k.Label != null && k.Label.StartsWith("initial")));
        });

        // Original keys still work.
        await fx.Client(fx.RootManagementKey).GetAsync("/v1/subtenants").ExpectAsync(HttpStatusCode.OK);
    }

    [Fact] // AC 7
    public async Task Child_created_without_email_can_set_it_with_its_own_key()
    {
        var child = await fx.CreateChildAsync();
        var client = fx.Client(child.Key);

        var config = await client.GetAsync("/v1/tenant/configuration").ExpectAsync(HttpStatusCode.OK);
        Assert.Null(config["contact_email"]);

        var updated = await client.PatchJsonAsync("/v1/tenant/configuration", new { contact_email = "me@example.com" })
            .ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("me@example.com", updated.Str("contact_email"));
        Assert.Equal("Test child", updated.Str("name"));

        // Explicit null clears it.
        var cleared = await client.PatchRawAsync("/v1/tenant/configuration", """{"contact_email": null}""")
            .ExpectAsync(HttpStatusCode.OK);
        Assert.Null(cleared["contact_email"]);
    }

    [Theory] // AC 7
    [InlineData("""{"contact_email": "not-an-email"}""")]
    [InlineData("""{"name": ""}""")]
    [InlineData("""{"name": "   "}""")]
    [InlineData("""{"name": null}""")]
    [InlineData("""{"name": 42}""")]
    [InlineData("""[]""")]
    [InlineData("""{not json""")]
    public async Task Invalid_configuration_is_rejected(string json)
    {
        var child = await fx.CreateChildAsync();
        await fx.Client(child.Key).PatchRawAsync("/v1/tenant/configuration", json)
            .ExpectErrorAsync(HttpStatusCode.BadRequest, "invalid_request");
    }

    [Theory] // AC 6
    [InlineData("""{"name": "x", "can_create_subtenants": true}""")]
    [InlineData("""{"name": "x", "can_create_campaigns": true}""")]
    [InlineData("""{"name": "x", "status": "active"}""")]
    [InlineData("""{"name": "x", "parent_tenant_id": null}""")]
    [InlineData("""{"name": "x", "id": "00000000-0000-0000-0000-000000000000"}""")]
    public async Task Extra_fields_cannot_change_capabilities_status_or_ownership(string json)
    {
        var child = await fx.CreateChildAsync();
        var client = fx.Client(child.Key);

        await client.PatchRawAsync("/v1/tenant/configuration", json)
            .ExpectErrorAsync(HttpStatusCode.BadRequest, "invalid_request");

        var tenant = await client.GetAsync("/v1/tenant").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Test child", tenant["configuration"]!.Str("name"));
        Assert.False(tenant["capabilities"]!["can_create_subtenants"]!.GetValue<bool>());
        Assert.Equal(fx.RootId.ToString(), tenant.Str("parent_tenant_id"));
    }

    [Fact] // AC 7
    public async Task Unrelated_tenants_may_share_an_email()
    {
        var a = await fx.CreateChildAsync(email: "shared@example.com");
        var b = await fx.CreateChildAsync(email: "shared@example.com");

        var tenantA = await fx.Client(a.Key).GetAsync("/v1/tenant").ExpectAsync(HttpStatusCode.OK);
        var tenantB = await fx.Client(b.Key).GetAsync("/v1/tenant").ExpectAsync(HttpStatusCode.OK);
        Assert.NotEqual(tenantA.Str("id"), tenantB.Str("id"));
    }

    [Fact] // AC 12
    public async Task Changes_are_audited_with_actor_and_without_secrets()
    {
        var child = await fx.CreateChildAsync();
        await fx.Client(child.Key).PatchJsonAsync("/v1/tenant/configuration", new { name = "Renamed" })
            .ExpectAsync(HttpStatusCode.OK);

        await fx.WithDbAsync(async db =>
        {
            var events = await db.AuditEvents.Where(e => e.TargetTenantId == child.Id).ToListAsync();
            Assert.Contains(events, e => e.Action == AuditActions.TenantCreated && e.ActorApiKeyId != null);
            Assert.Contains(events, e => e.Action == AuditActions.ApiKeyIssued);
            var update = Assert.Single(events, e => e.Action == AuditActions.TenantConfigurationUpdated);
            Assert.Equal(child.KeyId, update.ActorApiKeyId);
            Assert.Contains("name", update.Details);

            var allDetails = await db.AuditEvents.Select(e => e.Details).ToListAsync();
            Assert.DoesNotContain(allDetails, d => d.Contains("prt_") || d.Contains("secret"));
            Assert.All(await db.ApiKeys.ToListAsync(), k => Assert.Equal(32, k.SecretHash.Length));
        });
    }

    [Fact] // AC 13
    public async Task Data_and_keys_survive_an_application_restart()
    {
        var child = await fx.CreateChildAsync();

        await using var restarted = fx.CreateFactory();
        var tenant = await fx.Client(child.Key, restarted).GetAsync("/v1/tenant").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(child.Id.ToString(), tenant.Str("id"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("garbage")]
    [InlineData("prt_01a112649dcb7538bb1ede1dec72c96d_wrongsecret")]
    public async Task Missing_or_invalid_keys_get_401(string? key) =>
        await fx.Client(key).GetAsync("/v1/tenant").ExpectErrorAsync(HttpStatusCode.Unauthorized, "unauthorized");

    [Fact]
    public async Task Root_status_is_reported_through_current_tenant() =>
        Assert.Equal(TenantStatus.Active.ToString().ToLowerInvariant(),
            (await fx.Client(fx.RootTenantKey).GetAsync("/v1/tenant").ExpectAsync(HttpStatusCode.OK)).Str("status"));
}
