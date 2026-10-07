using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pairrot.Api.Endpoints;
using Pairrot.Core.Webhooks;
using Pairrot.Infrastructure.Persistence;
using Pairrot.Infrastructure.Webhooks;

namespace Pairrot.IntegrationTests;

/// <summary>
/// Features 010 and 011: routing payment changes to subscribers, signing, the fixed retry
/// schedule, holds, replay, key rotation, and permissions. A fake receiver replaces the network;
/// the shiftable clock replaces waiting.
/// </summary>
[Collection(PairrotCollection.Name)]
public class WebhookTests(PairrotFixture fx)
{
    private WebhookHarness W => fx.Webhooks;
    private ParsingHarness P => fx.Parsing;

    private sealed record Root(Guid Id, string TenantKey, string ManagementKey);

    private sealed record Subscriber(string Base, HttpClient Client, string Url, byte[] Secret, string KeyId);

    private async Task<Root> NewRootAsync()
    {
        var setup = await fx.SetupRootAsync(PairrotFixture.Unique("root"));
        return new Root(setup.Tenant.Id, setup.TenantKey!.Secret, setup.ManagementKey!.Secret);
    }

    private static HttpRequestMessage Command(HttpMethod method, string path, object? body, string? key = null)
    {
        var message = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        message.Headers.Add(MatchingEndpoints.IdempotencyKeyHeader, key ?? Guid.NewGuid().ToString());
        return message;
    }

    /// <summary>Creates an endpoint, activates it, and returns its one-time secret.</summary>
    private async Task<Subscriber> SubscribeAsync(string key, string basePath = "/v1/tenant", string scope = "own", bool activate = true)
    {
        var client = W.Client(key);
        var url = $"https://hooks-{Guid.NewGuid():N}.example.com/pairrot";
        var created = await client.SendAsync(Command(HttpMethod.Post, $"{basePath}/webhook-endpoint", new { url, scope, environment = "live" }))
            .ExpectAsync(HttpStatusCode.Created);
        var secret = WebhookSignature.ParseSecret(created["signing_secret"]!.Str("secret"))!;
        Assert.Equal("paused", created["endpoint"]!.Str("state"));
        if (activate)
        {
            await PatchAsync(client, basePath, new { state = "active" });
        }

        return new Subscriber(basePath, client, url, secret, created["signing_secret"]!.Str("key_id"));
    }

    private static async Task<JsonNode> PatchAsync(HttpClient client, string basePath, object body)
    {
        var current = await client.GetAsync($"{basePath}/webhook-endpoint").ExpectAsync(HttpStatusCode.OK);
        var patch = new HttpRequestMessage(HttpMethod.Patch, $"{basePath}/webhook-endpoint") { Content = JsonContent.Create(body) };
        patch.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{current["version"]}\""));
        return await client.SendAsync(patch).ExpectAsync(HttpStatusCode.OK);
    }

    /// <summary>A child with an account, an open request, and its exact payment parsed and matched.</summary>
    private async Task<(Guid TenantId, string Key, JsonNode Request)> PaidChildAsync(string managementKey, string vs = "2026000123")
    {
        var child = await fx.CreateChildAsync(managementKey: managementKey);
        var account = await P.CreateAccountAsync(child.Id, child.Key);
        var request = await W.Client(child.Key).PostAsJsonAsync("/v1/tenant/payment-requests", new
        {
            bank_connection_id = account.ConnectionId, external_id = $"booking-{Guid.NewGuid():N}", amount = 45000, currency = "CZK", variable_symbol = vs,
        }).ExpectAsync(HttpStatusCode.Created);
        await P.DeliverAsync(account, ParsingHarness.Incoming(account.Bank, vs: vs));
        return (child.Id, child.Key, request);
    }

    private bool Verifies(Subscriber subscriber, WebhookHttpRequest request, params (string KeyId, byte[] Secret)[] keys) =>
        WebhookSignature.Verify(
            (keys.Length == 0 ? [(subscriber.KeyId, subscriber.Secret)] : keys).ToDictionary(k => k.KeyId, k => k.Secret),
            FakeWebhookReceiver.Header(request, "Pairrot-Webhook-Id"),
            FakeWebhookReceiver.Header(request, "Pairrot-Webhook-Timestamp"),
            FakeWebhookReceiver.Header(request, "Pairrot-Webhook-Signature"),
            request.Body,
            W.Clock.GetUtcNow());

    private static JsonNode Body(WebhookHttpRequest request) => JsonNode.Parse(Encoding.UTF8.GetString(request.Body))!;

    [Fact] // AC 1, 2, 4
    public async Task A_parent_endpoint_receives_its_childrens_signed_payment_changes_and_children_see_only_their_own()
    {
        var root = await NewRootAsync();
        var parent = await SubscribeAsync(root.ManagementKey, "/v1/management", "own_and_direct_children");

        // An ordinary tenant key cannot subscribe to children.
        await W.Client(root.TenantKey).SendAsync(Command(HttpMethod.Post, "/v1/tenant/webhook-endpoint",
            new { url = "https://x.example.com/h", scope = "own_and_direct_children", environment = "live" })).ExpectErrorAsync(HttpStatusCode.Forbidden, "forbidden");

        var (childId, childKey, request) = await PaidChildAsync(root.ManagementKey);
        var child = await SubscribeAsync(childKey);
        Assert.Equal(1, await W.RunDeliveriesAsync() > 0 ? 1 : 0);

        // The parent got it; the child subscribed after the change and gets nothing old.
        var sent = Assert.Single(W.Receiver.To(parent.Url));
        Assert.Empty(W.Receiver.To(child.Url));
        Assert.True(Verifies(parent, sent));
        var body = Body(sent);
        Assert.Equal(("payment_request.updated", 1, "live", childId.ToString()),
            (body.Str("type"), body["schema_version"]!.GetValue<int>(), body.Str("environment"), body.Str("tenant_id")));
        var data = body["data"]!;
        Assert.Equal((request.Str("external_id"), request.Str("id"), 1, "allocation_created"),
            (data.Str("external_id"), data.Str("payment_request_id"), data["sequence"]!.GetValue<int>(), data.Str("cause")));
        Assert.Equal(("open", "paid", 45000L), (data["previous"]!.Str("status"), data["current"]!.Str("status"), data["current"]!["paid_amount"]!.GetValue<long>()));

        // Tampering, other keys, and stale timestamps fail verification.
        var tampered = sent with { Body = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(sent.Body).Replace("45000", "99999")) };
        Assert.False(Verifies(parent, tampered));
        Assert.False(Verifies(parent, sent, (parent.KeyId, new byte[32])));
        W.Clock.Advance(TimeSpan.FromMinutes(6));
        Assert.False(Verifies(parent, sent));

        // History: the parent's management view shows the child's delivery; the child sees none of the parent's.
        var deliveries = (await parent.Client.GetAsync("/v1/management/webhook-deliveries").ExpectAsync(HttpStatusCode.OK)).Items();
        var delivered = Assert.Single(deliveries)!;
        Assert.Equal(("delivered", childId.ToString(), 1), (delivered.Str("state"), delivered.Str("owner_tenant_id"), delivered["attempt_in_generation"]!.GetValue<int>()));
        Assert.Empty((await child.Client.GetAsync("/v1/tenant/webhook-deliveries").ExpectAsync(HttpStatusCode.OK)).Items());
        await child.Client.GetAsync($"/v1/tenant/webhook-deliveries/{delivered.Str("id")}").ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");

        // The parent's ordinary tenant key does not read child payloads.
        Assert.Empty((await W.Client(root.TenantKey).GetAsync("/v1/tenant/webhook-events").ExpectAsync(HttpStatusCode.OK)).Items());
        Assert.Single((await parent.Client.GetAsync("/v1/management/webhook-events").ExpectAsync(HttpStatusCode.OK)).Items());

        // A sibling's later payment goes to the parent and to nobody else.
        var (siblingId, _, _) = await PaidChildAsync(root.ManagementKey, "777");
        await W.RunDeliveriesAsync();
        Assert.Equal(2, W.Receiver.To(parent.Url).Count);
        Assert.Equal(siblingId.ToString(), Body(W.Receiver.To(parent.Url)[1]).Str("tenant_id"));
        Assert.Empty(W.Receiver.To(child.Url));

        // GETs never contain a secret; the request exposes its change sequence.
        var config = await parent.Client.GetAsync("/v1/management/webhook-endpoint").ExpectAsync(HttpStatusCode.OK);
        Assert.DoesNotContain("pwhsec_", config.ToJsonString());
        Assert.Equal("delivering", config["health"]!.Str("status"));
        var paid = await W.Client(childKey).GetAsync($"/v1/tenant/payment-requests/{request.Str("id")}").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(1, paid["payment_change_sequence"]!.GetValue<int>());
    }

    [Fact] // Feature 011 AC 1, 2, 5, 7
    public async Task A_failing_receiver_is_retried_after_5_seconds_60_seconds_and_1_hour_and_then_left_for_a_manual_retry()
    {
        var root = await NewRootAsync();
        var (_, childKey, _) = await PaidChildAsync(root.ManagementKey);
        var subscriber = await SubscribeAsync(childKey);

        // Old history is not sent without a replay; a new change is.
        await W.RunDeliveriesAsync();
        Assert.Empty(W.Receiver.To(subscriber.Url));
        var events = (await subscriber.Client.GetAsync("/v1/tenant/webhook-events").ExpectAsync(HttpStatusCode.OK)).Items();
        var endpoint = await subscriber.Client.GetAsync("/v1/tenant/webhook-endpoint").ExpectAsync(HttpStatusCode.OK);
        W.Receiver.Script(subscriber.Url, _ => FakeWebhookReceiver.Status(503), _ => FakeWebhookReceiver.Timeout(),
            _ => FakeWebhookReceiver.Status(429), _ => FakeWebhookReceiver.Status(500));
        await subscriber.Client.SendAsync(Command(HttpMethod.Post, "/v1/tenant/webhook-endpoint/replay", new
        {
            event_ids = new[] { Assert.Single(events)!.Str("id") },
            expected_destination_revision = endpoint["destination_revision"]!.GetValue<int>(),
            reason = "Initial sync",
        })).ExpectAsync(HttpStatusCode.Accepted);

        async Task<JsonNode> DeliveryAsync() =>
            Assert.Single((await subscriber.Client.GetAsync("/v1/tenant/webhook-deliveries").ExpectAsync(HttpStatusCode.OK)).Items())!;

        await W.RunDeliveriesAsync();
        Assert.Single(W.Receiver.To(subscriber.Url));
        var waiting = await DeliveryAsync();
        Assert.Equal(("retry_wait", 1, 4, "http_503"), (waiting.Str("state"), waiting["attempt_in_generation"]!.GetValue<int>(),
            waiting["max_attempts"]!.GetValue<int>(), waiting.Str("last_error_code")));

        foreach (var (early, rest, count) in new[] { (4, 2, 2), (55, 6, 3), (3590, 15, 4) })
        {
            W.Clock.Advance(TimeSpan.FromSeconds(early));
            await W.RunDeliveriesAsync();
            Assert.Equal(count - 1, W.Receiver.To(subscriber.Url).Count); // not before its due time
            W.Clock.Advance(TimeSpan.FromSeconds(rest));
            await W.RunDeliveriesAsync();
            Assert.Equal(count, W.Receiver.To(subscriber.Url).Count);
        }

        // No fifth automatic attempt.
        W.Clock.Advance(TimeSpan.FromHours(3));
        await W.RunDeliveriesAsync();
        var sent = W.Receiver.To(subscriber.Url);
        Assert.Equal(4, sent.Count);
        var failed = await DeliveryAsync();
        Assert.Equal(("failed", "attempts_exhausted", 4), (failed.Str("state"), failed.Str("last_outcome"), failed["total_attempts"]!.GetValue<int>()));

        // Every attempt has the same delivery ID and bytes, with a fresh timestamp and signature.
        Assert.Single(sent.Select(r => FakeWebhookReceiver.Header(r, "Pairrot-Webhook-Id")).Distinct());
        Assert.Single(sent.Select(r => Convert.ToBase64String(r.Body)).Distinct());
        Assert.Equal(4, sent.Select(r => FakeWebhookReceiver.Header(r, "Pairrot-Webhook-Signature")).Distinct().Count());

        // A manual retry starts one new generation; repeating the command adds nothing.
        var key = Guid.NewGuid().ToString();
        var retry = new { expected_version = failed["version"]!.GetValue<int>(), reason = "Receiver fixed" };
        await subscriber.Client.SendAsync(Command(HttpMethod.Post, $"/v1/tenant/webhook-deliveries/{failed.Str("id")}/retry", retry, key)).ExpectAsync(HttpStatusCode.Accepted);
        await subscriber.Client.SendAsync(Command(HttpMethod.Post, $"/v1/tenant/webhook-deliveries/{failed.Str("id")}/retry", retry, key)).ExpectAsync(HttpStatusCode.Accepted);
        await W.RunDeliveriesAsync();
        var detail = await subscriber.Client.GetAsync($"/v1/tenant/webhook-deliveries/{failed.Str("id")}").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(("delivered", 2, 5), (detail["delivery"]!.Str("state"), detail["delivery"]!["generation"]!.GetValue<int>(), detail["delivery"]!["total_attempts"]!.GetValue<int>()));
        Assert.Equal(["retryable_failure", "retryable_failure", "retryable_failure", "retryable_failure", "delivered"],
            detail["attempts"]!.AsArray().Select(a => a!.Str("outcome")));
        Assert.Equal(5, W.Receiver.To(subscriber.Url).Count);
    }

    [Fact] // Feature 010 AC 6, Feature 011 AC 2, 3
    public async Task Permanent_errors_stop_at_once_retry_after_only_delays_and_a_crash_counts_as_an_unknown_attempt()
    {
        var root = await NewRootAsync();
        var parent = await SubscribeAsync(root.ManagementKey, "/v1/management", "own_and_direct_children");

        // 400 and a redirect are permanent: no automatic retry.
        W.Receiver.Script(parent.Url, _ => FakeWebhookReceiver.Status(400), _ => FakeWebhookReceiver.Status(302));
        await PaidChildAsync(root.ManagementKey, "1");
        await PaidChildAsync(root.ManagementKey, "2");
        await W.RunDeliveriesAsync();
        W.Clock.Advance(TimeSpan.FromHours(2));
        await W.RunDeliveriesAsync();
        Assert.Equal(2, W.Receiver.To(parent.Url).Count);
        var failed = (await parent.Client.GetAsync("/v1/management/webhook-deliveries?state=failed").ExpectAsync(HttpStatusCode.OK)).Items();
        Assert.Equal(["http_400", "redirect_not_followed"], failed.Select(d => d!.Str("last_error_code")).Order());

        // Retry-After later than the schedule delays the retry, but adds no attempt.
        W.Receiver.Script(parent.Url, _ => FakeWebhookReceiver.Status(429, "120"));
        await PaidChildAsync(root.ManagementKey, "3");
        await W.RunDeliveriesAsync();
        W.Clock.Advance(TimeSpan.FromSeconds(10));
        await W.RunDeliveriesAsync();
        Assert.Equal(3, W.Receiver.To(parent.Url).Count);
        W.Clock.Advance(TimeSpan.FromSeconds(115));
        await W.RunDeliveriesAsync();
        Assert.Equal(4, W.Receiver.To(parent.Url).Count);

        // The worker dies after the receiver accepted: the attempt is unknown, consumes budget, and is retried.
        W.Receiver.Script(parent.Url, _ => throw new WorkerCrash());
        await PaidChildAsync(root.ManagementKey, "4");
        await W.RunDeliveriesAsync();
        Assert.Equal(5, W.Receiver.To(parent.Url).Count);
        W.Clock.Advance(TimeSpan.FromSeconds(61)); // the 60-second lease expires
        await W.RunDeliveriesAsync(); // recovery: outcome unknown, retry due 5 s after the lease expiry
        W.Clock.Advance(TimeSpan.FromSeconds(5));
        await W.RunDeliveriesAsync();
        var sent = W.Receiver.To(parent.Url);
        Assert.Equal(6, sent.Count);
        Assert.Equal(FakeWebhookReceiver.Header(sent[4], "Pairrot-Webhook-Id"), FakeWebhookReceiver.Header(sent[5], "Pairrot-Webhook-Id"));
        var crashed = (await parent.Client.GetAsync("/v1/management/webhook-deliveries?state=delivered").ExpectAsync(HttpStatusCode.OK)).Items()
            .Single(d => d!.Str("id") == FakeWebhookReceiver.Header(sent[5], "Pairrot-Webhook-Id"))!;
        var attempts = (await parent.Client.GetAsync($"/v1/management/webhook-deliveries/{crashed.Str("id")}").ExpectAsync(HttpStatusCode.OK))["attempts"]!.AsArray();
        Assert.Equal(["outcome_unknown", "delivered"], attempts.Select(a => a!.Str("outcome")));
    }

    [Fact] // Feature 010 AC 5, 8, 9
    public async Task Pause_url_changes_replay_rotation_and_tests_follow_their_rules()
    {
        var root = await NewRootAsync();
        var parent = await SubscribeAsync(root.ManagementKey, "/v1/management", "own_and_direct_children");
        var api = "/v1/management";

        // Paused: work accumulates; a test probe still goes out.
        await PatchAsync(parent.Client, api, new { state = "paused" });
        await PaidChildAsync(root.ManagementKey, "10");
        await parent.Client.SendAsync(Command(HttpMethod.Post, $"{api}/webhook-endpoint/test", null)).ExpectAsync(HttpStatusCode.Accepted);
        await W.RunDeliveriesAsync();
        var probe = Assert.Single(W.Receiver.To(parent.Url));
        Assert.Equal(("webhook.test", "test", true), (Body(probe).Str("type"), Body(probe).Str("environment"), Body(probe)["data"]!["synthetic"]!.GetValue<bool>()));
        Assert.NotNull((await parent.Client.GetAsync($"{api}/webhook-endpoint").ExpectAsync(HttpStatusCode.OK))["health"]!["last_test_accepted_at"]);

        // A new URL holds the backlog for the old destination; resuming does not send it there.
        var newUrl = $"https://new-{Guid.NewGuid():N}.example.com/pairrot";
        var moved = await PatchAsync(parent.Client, api, new { url = newUrl, state = "active" });
        Assert.Equal(2, moved["destination_revision"]!.GetValue<int>());
        await W.RunDeliveriesAsync();
        Assert.Single(W.Receiver.To(parent.Url));
        Assert.Empty(W.Receiver.To(newUrl));
        var held = Assert.Single((await parent.Client.GetAsync($"{api}/webhook-deliveries?state=held").ExpectAsync(HttpStatusCode.OK)).Items())!;
        Assert.Equal("destination_changed", held.Str("hold_reason"));
        await parent.Client.SendAsync(Command(HttpMethod.Post, $"{api}/webhook-deliveries/{held.Str("id")}/retry",
            new { expected_version = held["version"]!.GetValue<int>(), reason = "x" })).ExpectErrorAsync(HttpStatusCode.Conflict, "destination_changed");

        // An explicit replay to the shown revision sends the same delivery ID and bytes to the new URL.
        await parent.Client.SendAsync(Command(HttpMethod.Post, $"{api}/webhook-endpoint/replay",
            new { event_ids = new[] { held.Str("event_id") }, expected_destination_revision = 1, reason = "x" })).ExpectErrorAsync(HttpStatusCode.PreconditionFailed, "precondition_failed");
        await parent.Client.SendAsync(Command(HttpMethod.Post, $"{api}/webhook-endpoint/replay",
            new { event_ids = new[] { Guid.NewGuid() }, expected_destination_revision = 2, reason = "x" })).ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");
        await parent.Client.SendAsync(Command(HttpMethod.Post, $"{api}/webhook-endpoint/replay",
            new { event_ids = new[] { held.Str("event_id") }, expected_destination_revision = 2, reason = "Moved receiver" })).ExpectAsync(HttpStatusCode.Accepted);
        await W.RunDeliveriesAsync();
        var replayed = Assert.Single(W.Receiver.To(newUrl));
        Assert.Equal(held.Str("id"), FakeWebhookReceiver.Header(replayed, "Pairrot-Webhook-Id"));

        // Rotation: both keys sign during the grace period; a zero grace revokes the old key at once.
        var endpoint = await parent.Client.GetAsync($"{api}/webhook-endpoint").ExpectAsync(HttpStatusCode.OK);
        var rotated = await parent.Client.SendAsync(Command(HttpMethod.Post, $"{api}/webhook-endpoint/rotate-secret",
            new { expected_version = endpoint["version"]!.GetValue<int>() })).ExpectAsync(HttpStatusCode.OK);
        var newKey = (rotated["signing_secret"]!.Str("key_id"), WebhookSignature.ParseSecret(rotated["signing_secret"]!.Str("secret"))!);
        await PaidChildAsync(root.ManagementKey, "11");
        await W.RunDeliveriesAsync();
        var overlapping = W.Receiver.To(newUrl)[^1];
        Assert.Equal(2, FakeWebhookReceiver.Header(overlapping, "Pairrot-Webhook-Signature").Split(' ').Length);
        Assert.True(Verifies(parent, overlapping));
        Assert.True(Verifies(parent, overlapping, newKey));

        var again = await parent.Client.GetAsync($"{api}/webhook-endpoint").ExpectAsync(HttpStatusCode.OK);
        await parent.Client.SendAsync(Command(HttpMethod.Post, $"{api}/webhook-endpoint/rotate-secret",
            new { expected_version = again["version"]!.GetValue<int>() })).ExpectErrorAsync(HttpStatusCode.Conflict, "retiring_key_exists");
        var rotateKey = Guid.NewGuid().ToString();
        var revoked = await parent.Client.SendAsync(Command(HttpMethod.Post, $"{api}/webhook-endpoint/rotate-secret",
            new { expected_version = again["version"]!.GetValue<int>(), grace_period_seconds = 0, revoke_retiring = true }, rotateKey)).ExpectAsync(HttpStatusCode.OK);
        // The same command again returns metadata only; the secret is never disclosed twice.
        var replay = await parent.Client.SendAsync(Command(HttpMethod.Post, $"{api}/webhook-endpoint/rotate-secret",
            new { expected_version = again["version"]!.GetValue<int>(), grace_period_seconds = 0, revoke_retiring = true }, rotateKey)).ExpectAsync(HttpStatusCode.OK);
        Assert.Null(replay["signing_secret"]);
        Assert.DoesNotContain("pwhsec_", replay.ToJsonString());
        var latest = (revoked["signing_secret"]!.Str("key_id"), WebhookSignature.ParseSecret(revoked["signing_secret"]!.Str("secret"))!);
        await PaidChildAsync(root.ManagementKey, "12");
        await W.RunDeliveriesAsync();
        var single = W.Receiver.To(newUrl)[^1];
        Assert.Single(FakeWebhookReceiver.Header(single, "Pairrot-Webhook-Signature").Split(' '));
        Assert.True(Verifies(parent, single, latest));
        Assert.False(Verifies(parent, single, newKey));

        // Secrets are encrypted at rest and never in audit details.
        await W.WithServicesAsync(async s =>
        {
            var db = s.GetRequiredService<PairrotDbContext>();
            var stored = await db.WebhookSigningKeys.AsNoTracking().Where(k => k.ProtectedSecret != null).Select(k => k.ProtectedSecret!).ToListAsync();
            Assert.DoesNotContain(stored, p => p.Contains(Convert.ToBase64String(latest.Item2)));
            var audit = await db.AuditEvents.AsNoTracking().Where(a => a.TargetTenantId == root.Id).Select(a => a.Details).ToListAsync();
            Assert.DoesNotContain(audit, d => d.Contains("pwhsec_") || d.Contains(Convert.ToBase64String(latest.Item2)));
            return 0;
        });

        // Abandoning keeps the history and stops attempts.
        W.Receiver.Script(newUrl, _ => FakeWebhookReceiver.Status(503));
        await PaidChildAsync(root.ManagementKey, "13");
        await W.RunDeliveriesAsync();
        var waiting = Assert.Single((await parent.Client.GetAsync($"{api}/webhook-deliveries?state=retry_wait").ExpectAsync(HttpStatusCode.OK)).Items())!;
        await parent.Client.SendAsync(Command(HttpMethod.Post, $"{api}/webhook-deliveries/{waiting.Str("id")}/abandon",
            new { expected_version = waiting["version"]!.GetValue<int>(), reason = "Reservation deleted" })).ExpectAsync(HttpStatusCode.Accepted);
        var count = W.Receiver.To(newUrl).Count;
        W.Clock.Advance(TimeSpan.FromMinutes(5));
        await W.RunDeliveriesAsync();
        Assert.Equal(count, W.Receiver.To(newUrl).Count);
    }

    [Fact] // Feature 010 AC 2, 4
    public async Task Cancellation_and_revocation_are_events_while_edits_and_creation_retries_are_not()
    {
        var root = await NewRootAsync();
        var child = await fx.CreateChildAsync(managementKey: root.ManagementKey);
        var subscriber = await SubscribeAsync(child.Key);
        var client = W.Client(child.Key);
        var account = await P.CreateAccountAsync(child.Id, child.Key);
        object RequestBody(string id) => new { bank_connection_id = account.ConnectionId, external_id = id, amount = 45000, currency = "CZK", variable_symbol = id.Length.ToString() + "55" };
        var created = await client.PostAsJsonAsync("/v1/tenant/payment-requests", RequestBody("cancel-me")).ExpectAsync(HttpStatusCode.Created);
        await client.PostAsJsonAsync("/v1/tenant/payment-requests", RequestBody("cancel-me")).ExpectAsync(HttpStatusCode.OK);
        var patch = new HttpRequestMessage(HttpMethod.Patch, $"/v1/tenant/payment-requests/{created.Str("id")}") { Content = JsonContent.Create(new { description = "x" }) };
        patch.Headers.IfMatch.Add(new EntityTagHeaderValue("\"1\""));
        await client.SendAsync(patch).ExpectAsync(HttpStatusCode.OK);
        await W.RunDeliveriesAsync();
        Assert.Empty(W.Receiver.To(subscriber.Url));

        var cancel = new HttpRequestMessage(HttpMethod.Post, $"/v1/tenant/payment-requests/{created.Str("id")}/cancel") { Content = JsonContent.Create(new { reason = "No show" }) };
        cancel.Headers.IfMatch.Add(new EntityTagHeaderValue("\"2\""));
        await client.SendAsync(cancel).ExpectAsync(HttpStatusCode.OK);
        await W.RunDeliveriesAsync();
        var data = Body(Assert.Single(W.Receiver.To(subscriber.Url)))["data"]!;
        Assert.Equal(("request_cancelled", "cancelled"), (data.Str("cause"), data["current"]!.Str("status")));
        Assert.Null(data["current"]!["remaining_amount"]);
    }

    [Fact] // Feature 012 AC 11
    public async Task A_generated_symbol_is_matched_exactly_and_reported_with_the_original_reference()
    {
        var child = await fx.CreateChildAsync();
        var subscriber = await SubscribeAsync(child.Key);
        var account = await P.CreateAccountAsync(child.Id, child.Key);
        var externalId = $"booking-{Guid.NewGuid():N}";
        var request = await W.Client(child.Key).PostAsJsonAsync("/v1/tenant/payment-requests", new
        {
            bank_connection_id = account.ConnectionId, external_id = externalId, amount = 45000, currency = "CZK",
        }).ExpectAsync(HttpStatusCode.Created);
        var vs = request.Str("variable_symbol");
        Assert.Equal("1", vs);

        await P.DeliverAsync(account, ParsingHarness.Incoming(account.Bank, vs: vs));
        await W.RunDeliveriesAsync();

        var sent = Assert.Single(W.Receiver.To(subscriber.Url));
        Assert.True(Verifies(subscriber, sent));
        var data = Body(sent)["data"]!;
        Assert.Equal((externalId, request.Str("id"), "paid"), (data.Str("external_id"), data.Str("payment_request_id"), data["current"]!.Str("status")));
    }
}
