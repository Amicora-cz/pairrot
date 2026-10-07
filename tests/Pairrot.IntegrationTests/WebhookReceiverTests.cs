using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Pairrot.Api.Endpoints;
using Pairrot.Core.Webhooks;
using Pairrot.Infrastructure.Webhooks;

namespace Pairrot.IntegrationTests;

/// <summary>
/// The real HTTP transport against a local receiver that follows the documented contract
/// (docs/webhooks-receiver.md): verify the raw body, deduplicate by event ID, and apply only a
/// newer sequence. The receiver is allowlisted the way a Development/Test setting permits.
/// </summary>
[Collection(PairrotCollection.Name)]
public class WebhookReceiverTests(PairrotFixture fx)
{
    /// <summary>A minimal NaLekci-like receiver: reservations keyed by payment request, with event receipts.</summary>
    private sealed class Receiver
    {
        public Dictionary<string, byte[]> Keys { get; } = [];
        public HashSet<string> HandledEvents { get; } = [];
        public Dictionary<string, (int Sequence, string Status, long Paid)> Reservations { get; } = [];
        public int Requests;
        public int Rejected;
        public bool Outage;

        public async Task<IResult> HandleAsync(HttpRequest request)
        {
            Interlocked.Increment(ref Requests);
            using var buffer = new MemoryStream();
            await request.Body.CopyToAsync(buffer);
            var body = buffer.ToArray();
            if (Outage)
            {
                return Results.StatusCode(503);
            }

            lock (this)
            {
                // 1. Signature and timestamp on the raw body, before parsing.
                if (!WebhookSignature.Verify(Keys, request.Headers["Pairrot-Webhook-Id"], request.Headers["Pairrot-Webhook-Timestamp"],
                        request.Headers["Pairrot-Webhook-Signature"], body, DateTimeOffset.UtcNow + Clock))
                {
                    Rejected++;
                    return Results.StatusCode(401);
                }

                var json = JsonNode.Parse(body)!;
                var eventId = json["id"]!.GetValue<string>();
                if (json["type"]!.GetValue<string>() != "payment_request.updated" || !HandledEvents.Add(eventId))
                {
                    return Results.Ok(); // tests and duplicates are acknowledged without business effect
                }

                var data = json["data"]!;
                var requestId = data["payment_request_id"]!.GetValue<string>();
                var sequence = data["sequence"]!.GetValue<int>();
                if (!Reservations.TryGetValue(requestId, out var current) || sequence > current.Sequence)
                {
                    Reservations[requestId] = (sequence, data["current"]!["status"]!.GetValue<string>(), data["current"]!["paid_amount"]!.GetValue<long>());
                }

                return Results.Ok();
            }
        }

        public TimeSpan Clock;
    }

    [Fact] // Feature 010 AC 4, 7, 12 (local receiver); Feature 011 local demonstration
    public async Task A_signed_webhook_reaches_a_real_receiver_survives_an_outage_and_a_late_old_event_cannot_undo_a_correction()
    {
        var port = FreePort();
        var receiver = new Receiver { Clock = fx.Inbound.Clock.GetUtcNow() - DateTimeOffset.UtcNow };
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://localhost:{port}");
        await using var app = builder.Build();
        app.MapPost("/hook", (HttpRequest request) => receiver.HandleAsync(request));
        await app.StartAsync();

        await using var factory = fx.Parsing.Factory.WithWebHostBuilder(b => b.UseSetting("Webhooks:AllowedLocalDestinations:0", $"localhost:{port}"));
        var root = await fx.SetupRootAsync(PairrotFixture.Unique("root"));
        var child = await fx.CreateChildAsync(managementKey: root.ManagementKey!.Secret);
        var client = fx.Client(child.Key, factory);

        HttpRequestMessage Command(string path, object? body)
        {
            var message = new HttpRequestMessage(HttpMethod.Post, path) { Content = body is null ? null : JsonContent.Create(body) };
            message.Headers.Add(MatchingEndpoints.IdempotencyKeyHeader, Guid.NewGuid().ToString());
            return message;
        }

        async Task RunAsync()
        {
            receiver.Clock = fx.Inbound.Clock.GetUtcNow() - DateTimeOffset.UtcNow;
            using var scope = factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<WebhookDeliveryWorker>().RunDueAsync();
        }

        // Production rules still refuse a non-allowlisted private destination.
        await client.SendAsync(Command("/v1/tenant/webhook-endpoint", new { url = $"http://localhost:{port + 1}/hook", environment = "test" }))
            .ExpectErrorAsync(HttpStatusCode.BadRequest, "invalid_url");

        var created = await client.SendAsync(Command("/v1/tenant/webhook-endpoint", new { url = $"http://localhost:{port}/hook", environment = "live" }))
            .ExpectAsync(HttpStatusCode.Created);
        receiver.Keys[created["signing_secret"]!.Str("key_id")] = WebhookSignature.ParseSecret(created["signing_secret"]!.Str("secret"))!;
        var patch = new HttpRequestMessage(HttpMethod.Patch, "/v1/tenant/webhook-endpoint") { Content = JsonContent.Create(new { state = "active" }) };
        patch.Headers.IfMatch.Add(new EntityTagHeaderValue("\"1\""));
        await client.SendAsync(patch).ExpectAsync(HttpStatusCode.OK);

        // The receiver is down for the whole automatic schedule.
        receiver.Outage = true;
        var account = await fx.Parsing.CreateAccountAsync(child.Id, child.Key);
        var request = await client.PostAsJsonAsync("/v1/tenant/payment-requests", new
        {
            bank_connection_id = account.ConnectionId, external_id = "booking_1", amount = 45000, currency = "CZK", variable_symbol = "2026000123",
        }).ExpectAsync(HttpStatusCode.Created);
        await fx.Parsing.DeliverAsync(account, ParsingHarness.Incoming(account.Bank));
        await RunAsync();
        foreach (var wait in new[] { 6, 61, 3601 })
        {
            fx.Inbound.Clock.Advance(TimeSpan.FromSeconds(wait));
            await RunAsync();
        }

        Assert.Equal(4, receiver.Requests);
        var failed = Assert.Single((await client.GetAsync("/v1/tenant/webhook-deliveries?state=failed").ExpectAsync(HttpStatusCode.OK)).Items())!;

        // Back online: a manual retry delivers, and the reservation is paid.
        receiver.Outage = false;
        await client.SendAsync(Command($"/v1/tenant/webhook-deliveries/{failed.Str("id")}/retry",
            new { expected_version = failed["version"]!.GetValue<int>(), reason = "Receiver is back" })).ExpectAsync(HttpStatusCode.Accepted);
        await RunAsync();
        Assert.Equal((1, "paid", 45000L), receiver.Reservations[request.Str("id")]);
        Assert.Equal(0, receiver.Rejected);

        // A correction: the allocation is revoked; the reservation follows the newer sequence.
        var tx = Assert.Single((await client.GetAsync("/v1/tenant/transactions").ExpectAsync(HttpStatusCode.OK)).Items())!;
        var current = await client.GetAsync($"/v1/tenant/payment-requests/{request.Str("id")}").ExpectAsync(HttpStatusCode.OK);
        await client.SendAsync(Command($"/v1/tenant/allocations/{tx["matching"]!["allocation"]!.Str("id")}/revoke", new
        {
            expected_allocation_version = 1,
            expected_transaction_version = tx["version"]!.GetValue<int>(),
            expected_request_version = current["version"]!.GetValue<int>(),
            reason = "Paid by mistake for another booking",
        })).ExpectAsync(HttpStatusCode.OK);
        await RunAsync();
        Assert.Equal((2, "open", 0L), receiver.Reservations[request.Str("id")]);

        // Replaying the old paid event delivers it again with a fresh signature, but it cannot undo the correction.
        var events = (await client.GetAsync($"/v1/tenant/webhook-events?payment_request_id={request.Str("id")}").ExpectAsync(HttpStatusCode.OK)).Items();
        var first = events.Single(e => e!["sequence"]!.GetValue<int>() == 1)!;
        receiver.HandledEvents.Remove(first.Str("id")); // even a receiver that lost its receipt keeps the newer state
        await client.SendAsync(Command("/v1/tenant/webhook-endpoint/replay",
            new { event_ids = new[] { first.Str("id") }, expected_destination_revision = 1, reason = "Reconcile" })).ExpectAsync(HttpStatusCode.Accepted);
        await RunAsync();
        Assert.Equal((2, "open", 0L), receiver.Reservations[request.Str("id")]);
        Assert.Equal(0, receiver.Rejected);

        await app.StopAsync();
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
