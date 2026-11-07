using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Pairrot.Api.Endpoints;
using Pairrot.Core.Inbound;

namespace Pairrot.IntegrationTests;

/// <summary>
/// Feature 021 with <c>Sandbox:EmitAsLive</c>: a simulated payment is recorded as live, pays its
/// request with live evidence, and reaches live webhook endpoints only, so an integrating
/// application's live-only receiver runs unchanged against a local sandbox.
/// </summary>
[Collection(PairrotCollection.Name)]
public class SandboxEmitAsLiveTests(PairrotFixture fx)
{
    private const string Tenant = "/v1/tenant";
    private const string Requests = Tenant + "/payment-requests";

    private SandboxHarness S => fx.SandboxLive;

    private static HttpRequestMessage Command(HttpMethod method, string path, object? body)
    {
        var message = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        message.Headers.Add(MatchingEndpoints.IdempotencyKeyHeader, Guid.NewGuid().ToString());
        return message;
    }

    private async Task<string> SubscribeAsync(string key, string environment, string basePath = Tenant, string scope = "own", params string[] eventTypes)
    {
        var client = S.Client(key);
        var url = $"https://hooks-{Guid.NewGuid():N}.example.com/pairrot";
        await client.SendAsync(Command(HttpMethod.Post, $"{basePath}/webhook-endpoint",
            new { url, scope, environment, event_types = eventTypes })).ExpectAsync(HttpStatusCode.Created);
        var current = await client.GetAsync($"{basePath}/webhook-endpoint").ExpectAsync(HttpStatusCode.OK);
        var patch = new HttpRequestMessage(HttpMethod.Patch, $"{basePath}/webhook-endpoint") { Content = JsonContent.Create(new { state = "active" }) };
        patch.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{current["version"]}\""));
        await client.SendAsync(patch).ExpectAsync(HttpStatusCode.OK);
        return url;
    }

    private List<JsonNode> Sent(string url) => S.Receiver.To(url).Select(r => JsonNode.Parse(Encoding.UTF8.GetString(r.Body))!).ToList();

    [Fact]
    public async Task A_simulated_payment_is_live_pays_with_live_evidence_and_reaches_live_endpoints_only()
    {
        // The child has a test endpoint; its parent has a live one covering its children, as an integration has.
        var setup = await fx.SetupRootAsync(PairrotFixture.Unique("root"));
        var child = await fx.CreateChildAsync(managementKey: setup.ManagementKey!.Secret);
        var account = await S.CreateAccountAsync(child.Key);
        var request = await S.Client(child.Key).PostAsJsonAsync(Requests, new
        {
            bank_connection_id = account.ConnectionId, external_id = $"lesson-{Guid.NewGuid():N}", amount = 45000L, currency = "CZK",
            variable_symbol = "2026000001",
        }).ExpectAsync(HttpStatusCode.Created);
        var testUrl = await SubscribeAsync(child.Key, "test", eventTypes: ["payment_request.updated"]);
        var liveUrl = await SubscribeAsync(setup.ManagementKey.Secret, "live", "/v1/management", "own_and_direct_children", "payment_request.updated");

        var status = await S.Client(child.Key).GetAsync("/v1/sandbox").ExpectAsync(HttpStatusCode.OK);
        Assert.True(status["emit_as_live"]!.GetValue<bool>());

        var pay = new HttpRequestMessage(HttpMethod.Post, $"{Requests}/{request.Str("id")}/sandbox/pay") { Content = JsonContent.Create(new { }) };
        var accepted = await S.Client(child.Key).SendAsync(pay).ExpectAsync(HttpStatusCode.Accepted);
        await S.RunJobsAsync();
        await S.RunJobsAsync();
        await S.RunDeliveriesAsync();

        var paid = await S.Client(child.Key).GetAsync($"{Requests}/{request.Str("id")}").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("paid", paid.Str("status"));
        var transaction = Assert.Single(await S.TransactionsAsync(account.ConnectionId));
        Assert.Equal(InboundEnvironment.Live, transaction.Environment);
        var allocation = Assert.Single((await S.Client(child.Key).GetAsync($"{Requests}/{request.Str("id")}/allocations").ExpectAsync(HttpStatusCode.OK)).Items())!;
        Assert.Equal(("automatic", "live"), (allocation.Str("method"), allocation.Str("evidence")));
        var message = await S.Client(child.Key).GetAsync($"{Tenant}/inbound-messages/{accepted.Str("inbound_message_id")}").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(("sandbox", "live", "stub"), (message.Str("transport_kind"), message.Str("environment"), message.Str("provider_instance_id")));

        Assert.Empty(S.Receiver.To(testUrl));
        var sent = Assert.Single(Sent(liveUrl));
        Assert.Equal(("payment_request.updated", "live", child.Id.ToString()), (sent.Str("type"), sent.Str("environment"), sent.Str("tenant_id")));
    }

    [Fact]
    public void EmitAsLive_without_sandbox_mode_refuses_to_start()
    {
        using var factory = fx.CreateFactory().WithWebHostBuilder(builder => builder.UseSetting("Sandbox:EmitAsLive", "true"));
        var failure = Record.Exception(() => factory.CreateClient());
        Assert.NotNull(failure);
        Assert.Contains("Sandbox:EmitAsLive requires Sandbox:Enabled.", failure.ToString());
    }
}
