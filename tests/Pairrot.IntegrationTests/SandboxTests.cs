using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pairrot.Api.Endpoints;
using Pairrot.Core.Banking;
using Pairrot.Core.Inbound;
using Pairrot.Core.Parsing;
using Pairrot.Core.Tenants;
using Pairrot.Core.Transactions;
using Pairrot.Infrastructure.Inbound;
using Pairrot.Infrastructure.Sandbox;

namespace Pairrot.IntegrationTests;

/// <summary>
/// Feature 021 end to end: simulated payments are rendered as bank notifications and run through
/// the real intake, parsing, matching, and webhooks as test data. The sandbox host uses the real
/// bank profiles and the built-in <c>stub</c> test instance.
/// </summary>
[Collection(PairrotCollection.Name)]
public class SandboxTests(PairrotFixture fx)
{
    private const string Tenant = "/v1/tenant";
    private const string Requests = Tenant + "/payment-requests";
    private const string Connections = Tenant + "/bank-connections";

    private SandboxHarness S => fx.Sandbox;

    private async Task<(Guid Id, string Key, SandboxAccount Account)> NewChildAsync(string bankCode = "0800")
    {
        var child = await fx.CreateChildAsync();
        return (child.Id, child.Key, await S.CreateAccountAsync(child.Key, bankCode));
    }

    private static HttpRequestMessage Command(HttpMethod method, string path, object? body, string? idempotencyKey = null)
    {
        var message = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        message.Headers.Add(MatchingEndpoints.IdempotencyKeyHeader, idempotencyKey ?? Guid.NewGuid().ToString());
        return message;
    }

    private async Task<string> SubscribeAsync(
        string key, string environment, string basePath = Tenant, string scope = "own", params string[] eventTypes)
    {
        var client = S.Client(key);
        var url = $"https://hooks-{Guid.NewGuid():N}.example.com/pairrot";
        await client.SendAsync(Command(HttpMethod.Post, $"{basePath}/webhook-endpoint",
            new { url, scope, environment, event_types = eventTypes.Length == 0 ? null : eventTypes })).ExpectAsync(HttpStatusCode.Created);
        var current = await client.GetAsync($"{basePath}/webhook-endpoint").ExpectAsync(HttpStatusCode.OK);
        var patch = new HttpRequestMessage(HttpMethod.Patch, $"{basePath}/webhook-endpoint") { Content = JsonContent.Create(new { state = "active" }) };
        patch.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{current["version"]}\""));
        await client.SendAsync(patch).ExpectAsync(HttpStatusCode.OK);
        return url;
    }

    private List<JsonNode> Sent(string url) => S.Receiver.To(url).Select(r => JsonNode.Parse(Encoding.UTF8.GetString(r.Body))!).ToList();

    private async Task<JsonNode> CreateRequestAsync(SandboxAccount account, string vs, long amount = 45000) =>
        await S.Client(account.Key).PostAsJsonAsync(Requests, new
        {
            bank_connection_id = account.ConnectionId, external_id = $"lesson-{Guid.NewGuid():N}", amount, currency = "CZK", variable_symbol = vs,
        }).ExpectAsync(HttpStatusCode.Created);

    private async Task<JsonNode> PayAsync(SandboxAccount account, JsonNode request, object? body = null, string? idempotencyKey = null)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, $"{Requests}/{request.Str("id")}/sandbox/pay") { Content = JsonContent.Create(body ?? new { }) };
        if (idempotencyKey is not null)
        {
            message.Headers.Add(MatchingEndpoints.IdempotencyKeyHeader, idempotencyKey);
        }

        var accepted = await S.Client(account.Key).SendAsync(message).ExpectAsync(HttpStatusCode.Accepted);
        await ProcessAsync();
        return accepted;
    }

    private Task<HttpResponseMessage> IncomingAsync(SandboxAccount account, object body, string? idempotencyKey = null, HttpClient? client = null)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, $"{Connections}/{account.ConnectionId}/sandbox/incoming-payments")
        {
            Content = JsonContent.Create(body),
        };
        if (idempotencyKey is not null)
        {
            message.Headers.Add(MatchingEndpoints.IdempotencyKeyHeader, idempotencyKey);
        }

        return (client ?? S.Client(account.Key)).SendAsync(message);
    }

    private async Task<JsonNode> SimulateAsync(SandboxAccount account, object body, string? idempotencyKey = null)
    {
        var accepted = await IncomingAsync(account, body, idempotencyKey).ExpectAsync(HttpStatusCode.Accepted);
        await ProcessAsync();
        return accepted;
    }

    private async Task ProcessAsync()
    {
        await S.RunJobsAsync();
        await S.RunJobsAsync();
        await S.RunDeliveriesAsync();
    }

    private async Task<JsonNode> GetRequestAsync(SandboxAccount account, JsonNode request) =>
        await S.Client(account.Key).GetAsync($"{Requests}/{request.Str("id")}").ExpectAsync(HttpStatusCode.OK);

    private static (string Status, long Paid, long? Remaining, long Overpaid) Summary(JsonNode request) => (
        request.Str("status"), request["paid_amount"]!.GetValue<long>(), request["remaining_amount"]?.GetValue<long>(),
        request["overpaid_amount"]!.GetValue<long>());

    private async Task<JsonNode> GetTransactionAsync(SandboxAccount account, JsonNode accepted)
    {
        var run = await ParseRunAsync(account, Guid.Parse(accepted.Str("inbound_message_id")));
        return await S.Client(account.Key).GetAsync($"{Tenant}/transactions/{run.Str("transaction_id")}").ExpectAsync(HttpStatusCode.OK);
    }

    private async Task AllocateAsync(SandboxAccount account, JsonNode transaction, JsonNode request)
    {
        var current = await GetRequestAsync(account, request);
        await S.Client(account.Key).SendAsync(Command(HttpMethod.Post, $"{Tenant}/transactions/{transaction.Str("id")}/allocations", new
        {
            payment_request_id = request.Str("id"),
            expected_transaction_version = transaction["version"]!.GetValue<int>(),
            expected_request_version = current["version"]!.GetValue<int>(),
            reason = "Sandbox part payment",
        })).ExpectAsync(HttpStatusCode.Created);
    }

    private async Task<JsonNode> ParseRunAsync(SandboxAccount account, Guid messageId) =>
        Assert.Single((await S.Client(account.Key).GetAsync($"{Tenant}/inbound-messages/{messageId}/parse-runs").ExpectAsync(HttpStatusCode.OK))["runs"]!.AsArray())!;

    [Fact]
    public async Task An_exact_payment_runs_the_real_pipeline_and_reaches_test_endpoints_only()
    {
        // The child has a test endpoint; its parent has a live one that also covers its children.
        var setup = await fx.SetupRootAsync(PairrotFixture.Unique("root"));
        var child = await fx.CreateChildAsync(managementKey: setup.ManagementKey!.Secret);
        var (tenantId, key, account) = (child.Id, child.Key, await S.CreateAccountAsync(child.Key));
        var request = await CreateRequestAsync(account, "2026000001");
        var testUrl = await SubscribeAsync(key, "test", eventTypes: ["payment_request.updated", "transaction.recorded"]);
        var liveUrl = await SubscribeAsync(setup.ManagementKey.Secret, "live", "/v1/management", "own_and_direct_children",
            "payment_request.updated", "transaction.recorded");

        // The connection has no address yet; the simulation assigns the stub address itself.
        var accepted = await PayAsync(account, request);
        Assert.Equal(("ceska_sporitelna", "accepted", account.ConnectionId.ToString(), request.Str("id")),
            (accepted.Str("profile"), accepted.Str("status"), accepted.Str("bank_connection_id"), accepted.Str("payment_request_id")));

        Assert.Equal(("paid", 45000L, (long?)0L, 0L), Summary(await GetRequestAsync(account, request)));
        var transaction = Assert.Single(await S.TransactionsAsync(account.ConnectionId));
        Assert.Equal((45000L, "2026000001", InboundEnvironment.Test), (transaction.Amount, transaction.VariableSymbol, transaction.Environment));
        var allocation = Assert.Single((await S.Client(key).GetAsync($"{Requests}/{request.Str("id")}/allocations").ExpectAsync(HttpStatusCode.OK)).Items())!;
        Assert.Equal(("automatic", "active", "test"), (allocation.Str("method"), allocation.Str("state"), allocation.Str("evidence")));

        // The message is a received test message from the sandbox adapter, parsed by the real profile.
        var message = await S.Client(key).GetAsync($"{Tenant}/inbound-messages/{accepted.Str("inbound_message_id")}").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(("sandbox", "received", "test", "stub"), (message.Str("transport_kind"), message.Str("source_kind"), message.Str("environment"), message.Str("provider_instance_id")));
        var run = await ParseRunAsync(account, Guid.Parse(accepted.Str("inbound_message_id")));
        Assert.Equal(("parsed", "ceska_sporitelna", "trusted"), (run.Str("outcome"), run.Str("bank_profile"), run.Str("trust_decision")));

        // Everything it produced is test data: only the test endpoint hears about it.
        Assert.Empty(S.Receiver.To(liveUrl));
        var sent = Sent(testUrl);
        Assert.Equal(["payment_request.updated", "transaction.recorded"], sent.Select(e => e.Str("type")).Order());
        Assert.All(sent, e => Assert.Equal(("test", tenantId.ToString()), (e.Str("environment"), e.Str("tenant_id"))));

        // The audit event names the connection and the money, not people.
        var audit = await S.WithDbAsync(db => db.AuditEvents.AsNoTracking().Where(a => a.Action == AuditActions.SandboxPaymentSimulated && a.TargetTenantId == tenantId).ToListAsync());
        var details = JsonNode.Parse(Assert.Single(audit).Details)!;
        Assert.Equal((account.ConnectionId.ToString(), accepted.Str("inbound_message_id"), 45000L, "2026000001"),
            (details.Str("connection_id"), details.Str("message_id"), details["amount"]!.GetValue<long>(), details.Str("variable_symbol")));
    }

    [Fact]
    public async Task Partial_remainder_overpaid_wrong_symbol_and_outgoing_payments_behave_like_real_ones()
    {
        var (_, key, account) = await NewChildAsync();
        var request = await CreateRequestAsync(account, "2026000002");

        // Matching is exact, as for real receipts: a part payment is recorded but waits for a manual allocation.
        var part = await PayAsync(account, request, new { amount = 10000 });
        Assert.Equal(("open", 0L, (long?)45000L, 0L), Summary(await GetRequestAsync(account, request)));
        var partTransaction = await GetTransactionAsync(account, part);
        Assert.Equal(("unallocated", "amount_mismatch"), (partTransaction.Str("handling_state"), partTransaction["matching"]!.Str("result")));
        await AllocateAsync(account, partTransaction, request);
        Assert.Equal(("partially_paid", 10000L, (long?)35000L, 0L), Summary(await GetRequestAsync(account, request)));

        // Without an amount the simulation pays the remainder.
        var rest = await PayAsync(account, request);
        var restTransaction = await GetTransactionAsync(account, rest);
        Assert.Equal(35000L, restTransaction["amount"]!.GetValue<long>());
        await AllocateAsync(account, restTransaction, request);
        Assert.Equal(("paid", 45000L, (long?)0L, 0L), Summary(await GetRequestAsync(account, request)));

        // A paid request needs an explicit amount; a further payment overpays it once allocated.
        var again = new HttpRequestMessage(HttpMethod.Post, $"{Requests}/{request.Str("id")}/sandbox/pay") { Content = JsonContent.Create(new { }) };
        await S.Client(key).SendAsync(again).ExpectErrorAsync(HttpStatusCode.Conflict, "payment_request_already_paid");
        var extra = await PayAsync(account, request, new { amount = 5000 });
        await AllocateAsync(account, await GetTransactionAsync(account, extra), request);
        Assert.Equal(("overpaid", 50000L, (long?)0L, 5000L), Summary(await GetRequestAsync(account, request)));

        // Another symbol finds no request: an unallocated receipt that, being test data, stays off the live attention list.
        var wrong = await SimulateAsync(account, new { amount = 12300, variable_symbol = "999", payer_name = "Jana Nováková", message = "Lekce" });
        var unmatched = await GetTransactionAsync(account, wrong);
        Assert.Equal(("unallocated", "request_not_found", "test"),
            (unmatched.Str("handling_state"), unmatched["matching"]!.Str("result"), unmatched.Str("evidence")));
        var attention = (await S.Client(key).GetAsync($"{Tenant}/attention").ExpectAsync(HttpStatusCode.OK)).Items();
        Assert.DoesNotContain(attention, i => i!.Str("kind") == "unmatched_transaction");

        // A debit is recognized but is not a payment: no transaction.
        var before = (await S.TransactionsAsync(account.ConnectionId)).Count;
        var outgoing = await SimulateAsync(account, new { amount = 100, variable_symbol = "2026000002", direction = "outgoing" });
        var debit = await ParseRunAsync(account, Guid.Parse(outgoing.Str("inbound_message_id")));
        Assert.Equal(("not_payment", "outgoing_transfer"), (debit.Str("outcome"), debit.Str("reason_code")));
        Assert.Equal(before, (await S.TransactionsAsync(account.ConnectionId)).Count);
    }

    [Fact]
    public async Task The_sandbox_profile_serves_other_banks_and_supports_references()
    {
        var (_, key, account) = await NewChildAsync("0100");
        var request = await CreateRequestAsync(account, "2026000004");

        var first = await SimulateAsync(account, new
        {
            amount = 45000, variable_symbol = "2026000004", message = "Lekce 12. 11.", payer_name = "Jana Nováková",
            payer_account = "19-2000145399/0100", booked_at = "2026-10-20T09:15:00+02:00", bank_reference = "SBX-0001",
        });
        Assert.Equal("pairrot_sandbox", first.Str("profile"));
        var run = await ParseRunAsync(account, Guid.Parse(first.Str("inbound_message_id")));
        Assert.Equal(("parsed", "pairrot_sandbox", "1.0.0"), (run.Str("outcome"), run.Str("bank_profile"), run.Str("parser_version")));
        var transaction = Assert.Single(await S.TransactionsAsync(account.ConnectionId));
        Assert.Equal(("SBX-0001", "Jana Nováková", "Lekce 12. 11.", "2026-10-20"),
            (transaction.BankTransactionId, transaction.SenderName, transaction.Message, transaction.BookedOn.ToString("yyyy-MM-dd")));
        Assert.Equal("paid", (await GetRequestAsync(account, request)).Str("status"));

        // "The bank sent it twice": a second notification with the same reference is the same movement.
        var second = await SimulateAsync(account, new { amount = 45000, variable_symbol = "2026000004", booked_at = "2026-10-20T09:15:00+02:00", bank_reference = "SBX-0001" });
        var duplicate = await ParseRunAsync(account, Guid.Parse(second.Str("inbound_message_id")));
        Assert.Equal(("duplicate", transaction.Id.ToString()), (duplicate.Str("outcome"), duplicate.Str("transaction_id")));
        Assert.Single(await S.TransactionsAsync(account.ConnectionId));

        // The same reference with another amount disputes it instead of adding a payment.
        var conflicting = await SimulateAsync(account, new { amount = 46000, variable_symbol = "2026000004", booked_at = "2026-10-20T09:15:00+02:00", bank_reference = "SBX-0001" });
        var conflict = await ParseRunAsync(account, Guid.Parse(conflicting.Str("inbound_message_id")));
        Assert.Equal(("review_required", ParseReasons.BankReferenceConflict), (conflict.Str("outcome"), conflict.Str("reason_code")));
        Assert.Equal(IngestionStatus.Disputed, Assert.Single(await S.TransactionsAsync(account.ConnectionId)).IngestionStatus);

        // The status endpoint lists what can be simulated.
        var status = await S.Client(key).GetAsync("/v1/sandbox").ExpectAsync(HttpStatusCode.OK);
        Assert.True(status["enabled"]!.GetValue<bool>());
        Assert.Equal(["ceska_sporitelna", "pairrot_sandbox"], status["profiles"]!.AsArray().Select(p => p!.GetValue<string>()).Order());
    }

    [Fact]
    public async Task A_Ceska_sporitelna_connection_is_parsed_by_its_real_profile_which_has_no_bank_reference()
    {
        var (_, key, account) = await NewChildAsync();

        await IncomingAsync(account, new { amount = 100, bank_reference = "SBX-1" })
            .ExpectErrorAsync(HttpStatusCode.BadRequest, "field_not_supported_by_bank");

        var accepted = await SimulateAsync(account, new
        {
            amount = 123456, variable_symbol = "77", message = "Dobrý den", payer_name = "Jana Nováková", payer_account = "19-2000145399/0100",
            booked_at = "2026-10-25T09:15:00+01:00",
        });
        var run = await ParseRunAsync(account, Guid.Parse(accepted.Str("inbound_message_id")));
        Assert.Equal(("parsed", "ceska_sporitelna", "george_incoming_v1"), (run.Str("outcome"), run.Str("bank_profile"), run.Str("template_id")));
        var transaction = Assert.Single(await S.TransactionsAsync(account.ConnectionId));
        Assert.Equal((123456L, "77", "Dobrý den", "2026-10-25", "Jana Nováková"),
            (transaction.Amount, transaction.VariableSymbol, transaction.Message, transaction.BookedOn.ToString("yyyy-MM-dd"), transaction.SenderName));
        Assert.Equal("19-2000145399/0100", transaction.SenderAccount);

        // A booking time at the autumn clock change is the same moment on both sides of it.
        var late = await SimulateAsync(account, new { amount = 500, variable_symbol = "78", booked_at = "2026-10-24T23:30:00Z" });
        Assert.Equal("parsed", (await ParseRunAsync(account, Guid.Parse(late.Str("inbound_message_id")))).Str("outcome"));
        Assert.Equal("2026-10-25", (await S.TransactionsAsync(account.ConnectionId)).Single(t => t.VariableSymbol == "78").BookedOn.ToString("yyyy-MM-dd"));
    }

    [Fact]
    public async Task The_subtenant_route_works_with_a_management_key_and_other_tenants_connections_are_not_found()
    {
        var (childId, key, account) = await NewChildAsync();
        var request = await CreateRequestAsync(account, "2026000005");
        var parent = S.Client(fx.RootManagementKey);

        var viaParent = new HttpRequestMessage(HttpMethod.Post, $"/v1/subtenants/{childId}/bank-connections/{account.ConnectionId}/sandbox/incoming-payments")
        {
            Content = JsonContent.Create(new { amount = 45000, variable_symbol = "2026000005" }),
        };
        await parent.SendAsync(viaParent).ExpectAsync(HttpStatusCode.Accepted);
        var payViaParent = new HttpRequestMessage(HttpMethod.Post, $"/v1/subtenants/{childId}/payment-requests/{request.Str("id")}/sandbox/pay")
        {
            Content = JsonContent.Create(new { amount = 100 }),
        };
        await parent.SendAsync(payViaParent).ExpectAsync(HttpStatusCode.Accepted);
        await ProcessAsync();
        Assert.Equal(("paid", 45000L, (long?)0L, 0L), Summary(await GetRequestAsync(account, request)));
        Assert.Equal(2, (await S.TransactionsAsync(account.ConnectionId)).Count);

        // A tenant key cannot use the management route, and nobody reaches another tenant's connection.
        var intruder = await NewChildAsync();
        await S.Client(intruder.Key).SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/v1/subtenants/{childId}/bank-connections/{account.ConnectionId}/sandbox/incoming-payments")
        {
            Content = JsonContent.Create(new { amount = 100 }),
        }).ExpectAsync(HttpStatusCode.Forbidden);
        await IncomingAsync(account, new { amount = 100 }, client: S.Client(intruder.Key)).ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");
        await S.Client(intruder.Key).SendAsync(new HttpRequestMessage(HttpMethod.Post, $"{Requests}/{request.Str("id")}/sandbox/pay")
        {
            Content = JsonContent.Create(new { }),
        }).ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");
        await S.Client(RandomKey()).SendAsync(new HttpRequestMessage(HttpMethod.Post, $"{Requests}/{request.Str("id")}/sandbox/pay")
        {
            Content = JsonContent.Create(new { }),
        }).ExpectAsync(HttpStatusCode.Unauthorized);
    }

    private static string RandomKey() => "pk_invalid_" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task A_live_address_or_an_archived_connection_refuses_simulation()
    {
        var (tenantId, key, account) = await NewChildAsync();

        // An active address on a live instance: the connection is no sandbox target.
        await S.WithDbAsync(async db =>
        {
            db.InboundAddresses.Add(new InboundAddress
            {
                Id = Guid.CreateVersion7(), TenantId = tenantId, BankConnectionId = account.ConnectionId,
                Address = $"p_{Guid.NewGuid():N}"[..26] + "@live.pairrot-fixture.net", ProviderInstanceId = "live-fixture", ProviderKind = "fake",
                Environment = InboundEnvironment.Live, ProvisioningState = InboundProvisioningState.Ready, State = InboundAddressState.Active,
                CreatedAt = DateTimeOffset.UtcNow, ReadyAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
            return 0;
        });
        await IncomingAsync(account, new { amount = 100 }).ExpectErrorAsync(HttpStatusCode.Conflict, "sandbox_requires_test_address");
        Assert.Empty(await S.TransactionsAsync(account.ConnectionId));

        var archived = await S.CreateAccountAsync(key);
        await S.Client(key).DeleteAsync($"{Connections}/{archived.ConnectionId}").ExpectAsync(HttpStatusCode.NoContent);
        await IncomingAsync(archived, new { amount = 100 }).ExpectErrorAsync(HttpStatusCode.Conflict, "bank_connection_archived");
    }

    [Fact]
    public async Task A_repeated_idempotency_key_creates_one_message_and_without_one_every_call_is_new()
    {
        var (_, key, account) = await NewChildAsync("0100");
        var key1 = $"sbx-{Guid.NewGuid():N}";

        var first = await SimulateAsync(account, new { amount = 45000, variable_symbol = "11" }, key1);
        var retry = await SimulateAsync(account, new { amount = 45000, variable_symbol = "11" }, key1);
        Assert.Equal(first.Str("inbound_message_id"), retry.Str("inbound_message_id"));
        Assert.Single(await S.TransactionsAsync(account.ConnectionId));

        // The same key for another payment is a conflict, never a silent replay.
        await IncomingAsync(account, new { amount = 46000, variable_symbol = "11" }, key1).ExpectErrorAsync(HttpStatusCode.Conflict, "idempotency_key_conflict");
        await IncomingAsync(account, new { amount = 1 }, "has space").ExpectErrorAsync(HttpStatusCode.BadRequest, "invalid_idempotency_key");

        // Without the header, the bank "sends it twice": two messages, and the lookalike is flagged, not merged.
        var one = await SimulateAsync(account, new { amount = 7000, variable_symbol = "12" });
        var two = await SimulateAsync(account, new { amount = 7000, variable_symbol = "12" });
        Assert.NotEqual(one.Str("inbound_message_id"), two.Str("inbound_message_id"));
        Assert.Equal(3, (await S.TransactionsAsync(account.ConnectionId)).Count);
    }

    [Fact]
    public async Task Requests_are_validated_and_unknown_fields_are_refused()
    {
        var (_, _, account) = await NewChildAsync();

        (object Body, string Code, string Field)[] invalid =
        [
            (new { }, "invalid_amount", "amount"),
            (new { amount = 0 }, "invalid_amount", "amount"),
            (new { amount = 1_000_000_000 }, "invalid_amount", "amount"),
            (new { amount = 100, variable_symbol = "12345678901" }, "invalid_variable_symbol", "variable_symbol"),
            (new { amount = 100, variable_symbol = "12a" }, "invalid_variable_symbol", "variable_symbol"),
            (new { amount = 100, message = new string('x', 141) }, "invalid_message", "message"),
            (new { amount = 100, payer_account = "not an account" }, "invalid_payer_account", "payer_account"),
            (new { amount = 100, booked_at = "2026-10-25T09:15:00" }, "invalid_booked_at", "booked_at"),
            (new { amount = 100, direction = "sideways" }, "invalid_direction", "direction"),
        ];
        foreach (var (body, code, field) in invalid)
        {
            var error = await IncomingAsync(account, body).ExpectAsync(HttpStatusCode.BadRequest);
            Assert.Equal((code, field), (error["error"]!.Str("code"), error["error"]!.Str("field")));
        }

        await IncomingAsync(account, new { amount = 100, currency = "CZK" }).ExpectErrorAsync(HttpStatusCode.BadRequest, "invalid_request");
        await IncomingAsync(account, new { amount = "100" }).ExpectErrorAsync(HttpStatusCode.BadRequest, "invalid_request");
        Assert.Empty(await S.TransactionsAsync(account.ConnectionId));
    }

    [Fact]
    public async Task Without_sandbox_mode_no_route_exists_and_the_sandbox_profile_is_not_registered()
    {
        var child = await fx.CreateChildAsync();
        var client = fx.Client(child.Key);
        var bank = BankConnectionTests.NewAccount() with { BankCode = "0100" };
        var connection = await client.PostAsJsonAsync(Connections, new { account_number = bank.AccountNumber }).ExpectAsync(HttpStatusCode.Created);
        var path = $"{Connections}/{connection.Str("id")}/sandbox/incoming-payments";
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(path, new { amount = 100 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"{Requests}/{Guid.NewGuid()}/sandbox/pay", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/v1/sandbox")).StatusCode);

        Assert.Null(fx.Factory.Services.GetRequiredService<BankProfileRegistry>().For(bank));
        Assert.Equal(SandboxBankProfile.ProfileName, S.Factory.Services.GetRequiredService<BankProfileRegistry>().For(bank)!.Name);
        Assert.Equal(CeskaSporitelnaProfile.ProfileName, fx.Factory.Services.GetRequiredService<BankProfileRegistry>().For(bank with { BankCode = "0800" })!.Name);

        // Mail in the sandbox's format to such an account is foreign: kept as metadata only, never parsed.
        await client.PostAsync($"{Connections}/{connection.Str("id")}/inbound-address", null).ExpectAsync(HttpStatusCode.OK);
        var address = (await client.GetAsync($"{Connections}/{connection.Str("id")}").ExpectAsync(HttpStatusCode.OK)).Str("inbound_address");
        var notice = new SandboxProfileRenderer().Render(new SandboxPayment(
            bank, "CZK", 45000, DateTimeOffset.UtcNow, CzechBankAccount.ParseDomestic("19-2000145399/0100").Account!, "Payer", "1"));
        using var scope = fx.Factory.Services.CreateScope();
        var accepted = await scope.ServiceProvider.GetRequiredService<IInboundEmailIntake>().AcceptAsync(new InboundDelivery
        {
            ProviderInstanceId = "stub", SourceDeliveryId = $"d_{Guid.NewGuid():N}", SourceMessageId = $"m_{Guid.NewGuid():N}",
            EnvelopeRecipients = [address], RecipientEvidence = "test", HeaderFrom = notice.HeaderFrom, Subject = notice.Subject,
            TransportKind = "test", DeliveryEvidence = "test",
            Content = new InboundContentSnapshot(notice.Text, null, null, [address], null, new SenderAuthentication("pass", "pass", "pass", "test", "{}")),
        }, CancellationToken.None);
        Assert.Equal(IntakeStatus.Accepted, accepted.Status);
        var stored = await fx.Factory.Services.CreateScope().ServiceProvider.GetRequiredService<Pairrot.Infrastructure.Persistence.PairrotDbContext>()
            .InboundMessages.AsNoTracking().SingleAsync(m => m.Id == accepted.MessageId);
        Assert.Equal((InboundSenderClass.Foreign, InboundContentState.Skipped, null), (stored.SenderClass, stored.ContentState, stored.TextBody));
    }

    [Fact]
    public async Task Sandbox_mode_refuses_to_start_outside_development_and_testing()
    {
        using var production = fx.CreateFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Sandbox:Enabled", "true");
        });
        var failure = Record.Exception(() => production.CreateClient());
        Assert.NotNull(failure);
        Assert.Contains("Sandbox:Enabled is only allowed in Development and Testing; sandbox payments are never real.", failure.ToString());
    }
}
