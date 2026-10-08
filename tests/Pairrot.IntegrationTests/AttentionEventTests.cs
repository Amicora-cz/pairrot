using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pairrot.Api.Endpoints;
using Pairrot.Core.Inbound;
using Pairrot.Infrastructure.Inbound;
using Pairrot.Infrastructure.Webhooks;

namespace Pairrot.IntegrationTests;

/// <summary>
/// Feature 016: transaction, review case, and inbound address events with per-subject sequences,
/// event-type subscriptions, test evidence routed to test endpoints only, and the parent
/// attention list with its summary.
/// </summary>
[Collection(PairrotCollection.Name)]
public class AttentionEventTests(PairrotFixture fx)
{
    private const string Requests = "/v1/tenant/payment-requests";
    private const string Transactions = "/v1/tenant/transactions";
    private const string Cases = "/v1/tenant/review-cases";

    private static readonly string[] BusinessTypes =
        ["payment_request.updated", "review_case.opened", "review_case.updated", "transaction.recorded", "transaction.updated"];

    private WebhookHarness W => fx.Webhooks;
    private ParsingHarness P => fx.Parsing;

    private sealed record Subscriber(HttpClient Client, string Base, string Url);

    private static HttpRequestMessage Command(HttpMethod method, string path, object? body)
    {
        var message = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        message.Headers.Add(MatchingEndpoints.IdempotencyKeyHeader, Guid.NewGuid().ToString());
        return message;
    }

    private async Task<Subscriber> SubscribeAsync(
        string key, string basePath = "/v1/tenant", string scope = "own", string[]? eventTypes = null, string environment = "live")
    {
        var client = W.Client(key);
        var url = $"https://hooks-{Guid.NewGuid():N}.example.com/pairrot";
        await client.SendAsync(Command(HttpMethod.Post, $"{basePath}/webhook-endpoint", new { url, scope, environment, event_types = eventTypes }))
            .ExpectAsync(HttpStatusCode.Created);
        await PatchAsync(client, basePath, new { state = "active" });
        return new Subscriber(client, basePath, url);
    }

    private static async Task<JsonNode> PatchAsync(HttpClient client, string basePath, object body)
    {
        var current = await client.GetAsync($"{basePath}/webhook-endpoint").ExpectAsync(HttpStatusCode.OK);
        var patch = new HttpRequestMessage(HttpMethod.Patch, $"{basePath}/webhook-endpoint") { Content = JsonContent.Create(body) };
        patch.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{current["version"]}\""));
        return await client.SendAsync(patch).ExpectAsync(HttpStatusCode.OK);
    }

    private async Task<JsonNode> CreateRequestAsync(Account account, string vs, long amount = 45000, string? dueAt = null)
    {
        var created = await P.Client(account.Key).PostAsJsonAsync(Requests, new
        {
            bank_connection_id = account.ConnectionId, external_id = $"lesson-{Guid.NewGuid():N}", amount, currency = "CZK",
            variable_symbol = vs, due_at = dueAt,
        }).ExpectAsync(HttpStatusCode.Created);
        await P.RunJobsAsync();
        return created;
    }

    private async Task<JsonNode> GetAsync(string key, string path) => await P.Client(key).GetAsync(path).ExpectAsync(HttpStatusCode.OK);

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string path, object body) => client.SendAsync(Command(HttpMethod.Post, path, body));

    private Task<HttpResponseMessage> AllocateAsync(Account account, JsonNode transaction, JsonNode request, string reason) =>
        PostAsync(P.Client(account.Key), $"{Transactions}/{transaction.Str("id")}/allocations", new
        {
            payment_request_id = request.Str("id"),
            expected_transaction_version = transaction["version"]!.GetValue<int>(),
            expected_request_version = request["version"]!.GetValue<int>(),
            reason,
        });

    private static object Versions(JsonNode detail) => new Dictionary<string, int>(detail["transactions"]!.AsArray()
        .Select(t => KeyValuePair.Create(t!.Str("id"), t!["version"]!.GetValue<int>())));

    private List<JsonNode> Sent(Subscriber subscriber) =>
        W.Receiver.To(subscriber.Url).Select(r => JsonNode.Parse(Encoding.UTF8.GetString(r.Body))!).ToList();

    private static List<JsonNode> OfType(IEnumerable<JsonNode> bodies, string type) => bodies.Where(b => b.Str("type") == type).ToList();

    [Fact] // AC 1, 2, 3, 4
    public async Task Transaction_events_follow_matching_and_manual_allocation_and_reach_only_subscribed_endpoints()
    {
        var setup = await fx.SetupRootAsync(PairrotFixture.Unique("root"));
        var managementKey = setup.ManagementKey!.Secret;
        var child = await fx.CreateChildAsync(managementKey: managementKey);
        var account = await P.CreateAccountAsync(child.Id, child.Key);

        // An endpoint without event_types keeps receiving payment changes only.
        var own = await SubscribeAsync(child.Key);
        Assert.Equal(["payment_request.updated"], (await own.Client.GetAsync("/v1/tenant/webhook-endpoint").ExpectAsync(HttpStatusCode.OK))["event_types"]!
            .AsArray().Select(t => t!.GetValue<string>()));
        await W.Client(managementKey).SendAsync(Command(HttpMethod.Post, "/v1/management/webhook-endpoint", new
        {
            url = "https://hooks.example.com/x", scope = "own", environment = "live", event_types = new[] { "transaction.recorded", "bank_connection.reception_changed" },
        })).ExpectErrorAsync(HttpStatusCode.BadRequest, "invalid_event_types");
        var parent = await SubscribeAsync(managementKey, "/v1/management", "own_and_direct_children",
            ["transaction.updated", "transaction.recorded", "review_case.opened", "review_case.updated", "payment_request.updated", "transaction.recorded"]);
        Assert.Equal(BusinessTypes, (await parent.Client.GetAsync("/v1/management/webhook-endpoint").ExpectAsync(HttpStatusCode.OK))["event_types"]!
            .AsArray().Select(t => t!.GetValue<string>()));

        // An automatic match: transaction.recorded says matched, and the payment change follows as before.
        var exact = await CreateRequestAsync(account, "4242");
        await P.DeliverAsync(account, ParsingHarness.Incoming(account.Bank, vs: "4242"));
        await W.RunDeliveriesAsync();
        var sent = Sent(parent);
        Assert.Equal(["payment_request.updated", "transaction.recorded"], sent.Select(b => b.Str("type")).Order());
        var recorded = Assert.Single(OfType(sent, "transaction.recorded"))["data"]!;
        Assert.Equal(("recorded", 1, "allocated", "matched", exact.Str("id"), exact.Str("external_id")),
            (recorded.Str("cause"), recorded["sequence"]!.GetValue<int>(), recorded.Str("handling_state"), recorded["matching"]!.Str("result"),
                recorded["matching"]!.Str("payment_request_id"), recorded["matching"]!.Str("external_id")));
        Assert.Equal((45000L, "CZK", "4242", "2026-10-07"),
            (recorded["amount"]!.GetValue<long>(), recorded.Str("currency"), recorded.Str("variable_symbol"), recorded.Str("booked_on")));
        Assert.Equal(recorded.Str("transaction_id"), recorded.Str("subject_id"));
        Assert.Equal(["payment_request.updated"], Sent(own).Select(b => b.Str("type")));

        // A partial receipt suggests its request; a manual allocation then updates both subjects.
        var partial = await CreateRequestAsync(account, "5151");
        await P.DeliverAsync(account, ParsingHarness.Incoming(account.Bank, "300,00 CZK", "5151"));
        await W.RunDeliveriesAsync();
        var mismatch = OfType(Sent(parent), "transaction.recorded")[^1]["data"]!;
        Assert.Equal(("amount_mismatch", partial.Str("id"), "unallocated"),
            (mismatch["matching"]!.Str("result"), mismatch["matching"]!.Str("payment_request_id"), mismatch.Str("handling_state")));
        var transaction = await GetAsync(child.Key, $"{Transactions}/{mismatch.Str("transaction_id")}");
        var request = await GetAsync(child.Key, $"{Requests}/{partial.Str("id")}");
        await AllocateAsync(account, transaction, request, "Paid the rest in cash to the instructor").ExpectAsync(HttpStatusCode.Created);
        await W.RunDeliveriesAsync();
        var updated = Assert.Single(OfType(Sent(parent), "transaction.updated"))["data"]!;
        Assert.Equal(("allocated", 2, "allocated", mismatch.Str("subject_id")),
            (updated.Str("cause"), updated["sequence"]!.GetValue<int>(), updated.Str("handling_state"), updated.Str("subject_id")));
        Assert.NotNull(updated["allocation_id"]);
        Assert.Equal("allocation_created", OfType(Sent(parent), "payment_request.updated")[^1]["data"]!.Str("cause"));

        // No sender fields, e-mail content, or free-text reasons, neither sent nor stored.
        var stored = (await parent.Client.GetAsync("/v1/management/webhook-events?limit=100").ExpectAsync(HttpStatusCode.OK)).Items();
        foreach (var text in Sent(parent).Select(b => b.ToJsonString()).Concat(stored.Select(e => e!["body"]!.ToJsonString())))
        {
            Assert.DoesNotContain("2000145399", text);
            Assert.DoesNotContain("Nov", text);
            Assert.DoesNotContain("in cash", text);
        }

        // Opting out applies to later events; they are still recorded, and replay respects the subscription.
        await PatchAsync(parent.Client, "/v1/management", new { event_types = new[] { "payment_request.updated" } });
        var before = Sent(parent).Count;
        await P.DeliverAsync(account, ParsingHarness.Incoming(account.Bank, "120,00 CZK", "9090"));
        await W.RunDeliveriesAsync();
        Assert.Equal(before, Sent(parent).Count);
        var unsent = (await parent.Client.GetAsync("/v1/management/webhook-events?type=transaction.recorded").ExpectAsync(HttpStatusCode.OK)).Items();
        Assert.Equal(3, unsent.Count);
        Assert.Equal("request_not_found", unsent[0]!["body"]!["data"]!["matching"]!.Str("result"));
        var endpoint = await parent.Client.GetAsync("/v1/management/webhook-endpoint").ExpectAsync(HttpStatusCode.OK);
        await parent.Client.SendAsync(Command(HttpMethod.Post, "/v1/management/webhook-endpoint/replay", new
        {
            event_ids = new[] { unsent[0]!.Str("id") },
            expected_destination_revision = endpoint["destination_revision"]!.GetValue<int>(),
            reason = "Sync",
        })).ExpectErrorAsync(HttpStatusCode.Conflict, "event_type_not_subscribed");
        await parent.Client.GetAsync("/v1/management/webhook-events?type=bogus").ExpectErrorAsync(HttpStatusCode.BadRequest, "invalid_request");
        Assert.Single((await parent.Client.GetAsync("/v1/management/webhook-deliveries?type=transaction.updated").ExpectAsync(HttpStatusCode.OK)).Items());
    }

    [Fact] // AC 1, 6
    public async Task Review_and_address_events_carry_their_own_sequences_and_test_evidence_reaches_test_endpoints_only()
    {
        var setup = await fx.SetupRootAsync(PairrotFixture.Unique("root"));
        var managementKey = setup.ManagementKey!.Secret;
        var child = await fx.CreateChildAsync(managementKey: managementKey);
        var account = await P.CreateAccountAsync(child.Id, child.Key);
        var own = await SubscribeAsync(child.Key, eventTypes:
            ["review_case.opened", "review_case.updated", "transaction.updated", "inbound_address.paused", "inbound_address.resumed"]);

        // A lookalike opens a case and takes the first receipt's allocation away.
        await CreateRequestAsync(account, "4242");
        await P.DeliverAsync(account, ParsingHarness.Incoming(account.Bank, vs: "4242"));
        await P.DeliverAsync(account, ParsingHarness.Incoming(account.Bank, vs: "4242"));
        await W.RunDeliveriesAsync();
        var opened = Assert.Single(OfType(Sent(own), "review_case.opened"))["data"]!;
        Assert.Equal(("opened", 1, "possible_duplicate", "open", 2),
            (opened.Str("cause"), opened["sequence"]!.GetValue<int>(), opened.Str("issue"), opened.Str("state"), opened["transaction_ids"]!.AsArray().Count));
        var lostAllocation = Assert.Single(OfType(Sent(own), "transaction.updated"))["data"]!;
        Assert.Equal(("review_required", "unallocated", 2), (lostAllocation.Str("cause"), lostAllocation.Str("handling_state"), lostAllocation["sequence"]!.GetValue<int>()));

        // The decision: the case is resolved, the copy marked as a duplicate.
        var detail = await GetAsync(child.Key, $"{Cases}/{opened.Str("review_case_id")}");
        var canonical = lostAllocation.Str("transaction_id");
        await PostAsync(P.Client(child.Key), $"{Cases}/{opened.Str("review_case_id")}/resolve", new
        {
            decision = "mark_duplicate", reason = "Checked the statement: one payment", canonical_transaction_id = canonical,
            expected_version = detail["case"]!["version"]!.GetValue<int>(), expected_transaction_versions = Versions(detail),
        }).ExpectAsync(HttpStatusCode.OK);
        await W.RunDeliveriesAsync();
        var resolved = Assert.Single(OfType(Sent(own), "review_case.updated"))["data"]!;
        Assert.Equal(("resolved", 2, "resolved", "mark_duplicate"),
            (resolved.Str("cause"), resolved["sequence"]!.GetValue<int>(), resolved.Str("state"), resolved.Str("resolution")));
        Assert.NotNull(resolved["decision_id"]);
        Assert.Contains(OfType(Sent(own), "transaction.updated"), b => b["data"]!.Str("cause") == "marked_duplicate");
        Assert.DoesNotContain(Sent(own), b => b.ToJsonString().Contains("Checked the statement"));

        // Foreign mail pauses the address; the tenant resumes it.
        for (var i = 0; i < 6; i++)
        {
            await P.AcceptAsync(ParsingHarness.Delivery(account, $"Newsletter {i}", from: $"news{i}@shop.example"));
        }

        await P.Client(child.Key).PostAsync($"/v1/tenant/bank-connections/{account.ConnectionId}/inbound-address/resume", null).ExpectAsync(HttpStatusCode.OK);
        await W.RunDeliveriesAsync();
        var paused = Assert.Single(OfType(Sent(own), "inbound_address.paused"))["data"]!;
        Assert.Equal(("foreign_mail_limit", 1, "paused_foreign_mail", 6, false),
            (paused.Str("cause"), paused["sequence"]!.GetValue<int>(), paused.Str("address_processing_state"),
                paused["foreign_count_24h"]!.GetValue<int>(), paused["operator_only_resume"]!.GetValue<bool>()));
        var resumed = Assert.Single(OfType(Sent(own), "inbound_address.resumed"))["data"]!;
        Assert.Equal(("resumed_by_tenant", 2, "active", paused.Str("subject_id")),
            (resumed.Str("cause"), resumed["sequence"]!.GetValue<int>(), resumed.Str("address_processing_state"), resumed.Str("subject_id")));
        Assert.DoesNotContain("shop.example", string.Concat(Sent(own).Select(b => b.ToJsonString())));

        // Fixture evidence: its transaction event goes to a test endpoint, never to the parent's live one.
        var tester = await fx.CreateChildAsync(managementKey: managementKey);
        var fixture = await P.CreateAccountAsync(tester.Id, tester.Key, InboundHarness.InlineInstanceId);
        var testEndpoint = await SubscribeAsync(tester.Key, eventTypes: ["transaction.recorded"], environment: "test");
        var parent = await SubscribeAsync(managementKey, "/v1/management", "own_and_direct_children", ["transaction.recorded"]);
        await P.DeliverAsync(fixture, ParsingHarness.Incoming(fixture.Bank, vs: "777"));
        await W.RunDeliveriesAsync();
        var test = Assert.Single(Sent(testEndpoint));
        Assert.Equal(("test", "not_live_evidence"), (test.Str("environment"), test["data"]!["matching"]!.Str("result")));
        Assert.Empty(Sent(parent));
    }

    [Fact] // AC 5, 8
    public async Task The_attention_list_shows_what_waits_across_children_and_items_disappear_when_resolved()
    {
        var setup = await fx.SetupRootAsync(PairrotFixture.Unique("root"));
        var managementKey = setup.ManagementKey!.Secret;
        var management = W.Client(managementKey);
        var a = await fx.CreateChildAsync(managementKey: managementKey);
        var b = await fx.CreateChildAsync(managementKey: managementKey);
        var accountA = await P.CreateAccountAsync(a.Id, a.Key);
        var accountB = await P.CreateAccountAsync(b.Id, b.Key);

        // The parent's endpoint rejects everything, so its deliveries fail.
        var parent = await SubscribeAsync(managementKey, "/v1/management", "own_and_direct_children");
        W.Receiver.Script(parent.Url, Enumerable.Repeat<Func<WebhookHttpRequest, WebhookHttpResponse>>(_ => FakeWebhookReceiver.Status(400), 10).ToArray());

        // A: an unmatched receipt, a review case of two lookalikes, and an overdue request.
        await P.DeliverAsync(accountA, ParsingHarness.Incoming(accountA.Bank, vs: "777"));
        await P.DeliverAsync(accountA, ParsingHarness.Incoming(accountA.Bank, "250,00 CZK", "888"));
        await P.DeliverAsync(accountA, ParsingHarness.Incoming(accountA.Bank, "250,00 CZK", "888"));
        var overdue = await CreateRequestAsync(accountA, "999", dueAt: W.Clock.GetUtcNow().AddDays(-1).ToString("O"));

        // B: an overpaid request (a second receipt allocated by hand) and a paused address.
        var request = await CreateRequestAsync(accountB, "4242");
        await P.DeliverAsync(accountB, ParsingHarness.Incoming(accountB.Bank, vs: "4242"));
        await P.DeliverAsync(accountB, ParsingHarness.Incoming(accountB.Bank, "100,00 CZK", "4242"));
        var second = Assert.Single((await GetAsync(b.Key, $"{Transactions}?handling_state=unallocated")).Items())!;
        await AllocateAsync(accountB, second, await GetAsync(b.Key, $"{Requests}/{request.Str("id")}"), "Top-up").ExpectAsync(HttpStatusCode.Created);
        await fx.WithDbAsync(db => db.InboundAddresses.Where(x => x.Address == accountB.Address).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.Processing, InboundAddressProcessing.PausedForeignMail)
            .SetProperty(x => x.PausedAt, W.Clock.GetUtcNow())));
        await W.RunDeliveriesAsync();

        var items = (await management.GetAsync("/v1/management/attention").ExpectAsync(HttpStatusCode.OK)).Items();
        string Key(JsonNode? item) => $"{item!.Str("kind")}:{item!.Str("tenant_id")}";
        Assert.Equal(
            [$"failed_delivery:{b.Id}", $"failed_delivery:{b.Id}", $"overpaid_request:{b.Id}", $"paused_address:{b.Id}", $"review_case:{a.Id}",
                $"unmatched_transaction:{a.Id}"],
            items.Select(Key).Order());
        Assert.Equal(items.Select(i => i!["since"]!.GetValue<DateTimeOffset>()).Order(), items.Select(i => i!["since"]!.GetValue<DateTimeOffset>()));
        var unmatched = items.Single(i => i!.Str("kind") == "unmatched_transaction")!;
        Assert.Equal(("request_not_found", "777", 45000L), (unmatched["summary"]!.Str("result"), unmatched["summary"]!.Str("variable_symbol"),
            unmatched["summary"]!["amount"]!.GetValue<long>()));
        Assert.DoesNotContain("Nov", string.Concat(items.Select(i => i!.ToJsonString())));
        var overpaid = items.Single(i => i!.Str("kind") == "overpaid_request")!;
        Assert.Equal((request.Str("id"), 10000L), (overpaid.Str("subject_id"), overpaid["summary"]!["excess_amount"]!.GetValue<long>()));

        // Opt-in kinds, filters, the summary, and paging.
        var late = Assert.Single((await management.GetAsync("/v1/management/attention?kinds=overdue_request").ExpectAsync(HttpStatusCode.OK)).Items())!;
        Assert.Equal((overdue.Str("id"), a.Id.ToString()), (late.Str("subject_id"), late.Str("tenant_id")));
        Assert.Equal(2, (await management.GetAsync($"/v1/management/attention?tenant_id={a.Id}").ExpectAsync(HttpStatusCode.OK)).Items().Count);
        await management.GetAsync("/v1/management/attention?kinds=reception_not_ready").ExpectErrorAsync(HttpStatusCode.BadRequest, "invalid_request");
        var summary = await management.GetAsync("/v1/management/attention/summary").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal((6, 2, 1), (summary["total"]!.GetValue<int>(), summary["by_kind"]!["failed_delivery"]!.GetValue<int>(), summary["by_kind"]!["review_case"]!.GetValue<int>()));
        Assert.Equal(4, summary["by_tenant"]!.AsArray().Single(t => t!.Str("tenant_id") == b.Id.ToString())!["total"]!.GetValue<int>());
        var paged = new List<string>();
        string? cursor = null;
        do
        {
            var page = await management.GetAsync($"/v1/management/attention?limit=1{(cursor is null ? "" : $"&cursor={cursor}")}").ExpectAsync(HttpStatusCode.OK);
            paged.AddRange(page["items"]!.AsArray().Select(i => i!.Str("subject_id")));
            cursor = page["next_cursor"]?.GetValue<string>();
        }
        while (cursor is not null);
        Assert.Equal(items.Select(i => i!.Str("subject_id")), paged);

        // A child sees its own items only, never a sibling's, and no parent delivery failures.
        var own = (await P.Client(a.Key).GetAsync("/v1/tenant/attention").ExpectAsync(HttpStatusCode.OK)).Items();
        Assert.Equal(["review_case", "unmatched_transaction"], own.Select(i => i!.Str("kind")).Order());
        await P.Client(a.Key).GetAsync($"/v1/tenant/attention?tenant_id={b.Id}").ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");
        await W.Client(setup.TenantKey!.Secret).GetAsync("/v1/management/attention").ExpectAsync(HttpStatusCode.Forbidden);

        // Resolving each cause removes its item; the overpayment stays while the ledger records the excess.
        var transaction = await GetAsync(a.Key, $"{Transactions}/{unmatched.Str("subject_id")}");
        await PostAsync(P.Client(a.Key), $"{Transactions}/{transaction.Str("id")}/ignore",
            new { expected_version = transaction["version"]!.GetValue<int>(), reason = "Private transfer" }).ExpectAsync(HttpStatusCode.OK);
        var reviewCase = items.Single(i => i!.Str("kind") == "review_case")!.Str("subject_id");
        var detail = await GetAsync(a.Key, $"{Cases}/{reviewCase}");
        await PostAsync(P.Client(a.Key), $"{Cases}/{reviewCase}/resolve", new
        {
            decision = "mark_duplicate", reason = "One payment", canonical_transaction_id = detail["transactions"]![0]!.Str("id"),
            expected_version = detail["case"]!["version"]!.GetValue<int>(), expected_transaction_versions = Versions(detail),
        }).ExpectAsync(HttpStatusCode.OK);
        await P.Client(b.Key).PostAsync($"/v1/tenant/bank-connections/{accountB.ConnectionId}/inbound-address/resume", null).ExpectAsync(HttpStatusCode.OK);
        var remaining = (await management.GetAsync("/v1/management/attention").ExpectAsync(HttpStatusCode.OK)).Items();
        Assert.Equal([$"failed_delivery:{b.Id}", $"failed_delivery:{b.Id}", $"overpaid_request:{b.Id}", $"unmatched_transaction:{a.Id}"],
            remaining.Select(Key).Order());
        Assert.Equal(detail["transactions"]![0]!.Str("id"), remaining.Single(i => i!.Str("kind") == "unmatched_transaction")!.Str("subject_id"));

        // A deleted child contributes nothing.
        await management.DeleteAsync($"/v1/subtenants/{b.Id}").ExpectAsync(HttpStatusCode.NoContent);
        Assert.Equal([$"unmatched_transaction:{a.Id}"],
            (await management.GetAsync("/v1/management/attention").ExpectAsync(HttpStatusCode.OK)).Items().Select(Key));
    }
}
