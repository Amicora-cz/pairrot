using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pairrot.Api.Endpoints;
using Pairrot.Core.Inbound;
using Pairrot.Infrastructure.Matching;
using Pairrot.Tests.Shared;

namespace Pairrot.IntegrationTests;

/// <summary>
/// Feature 009 end to end with the synthetic test profile: exact automatic matching, manual
/// allocations and corrections, review of duplicates, disputes, and unverified candidates,
/// idempotent commands, and the payment-change journal.
/// </summary>
[Collection(PairrotCollection.Name)]
public class PaymentMatchingTests(PairrotFixture fx)
{
    private const string Tenant = "/v1/tenant";
    private const string Requests = Tenant + "/payment-requests";
    private const string Transactions = Tenant + "/transactions";
    private const string Cases = Tenant + "/review-cases";

    private ParsingHarness P => fx.Parsing;

    private async Task<Account> NewAccountAsync(string instance = ParsingHarness.SmtpInstanceId)
    {
        var child = await fx.CreateChildAsync();
        return await P.CreateAccountAsync(child.Id, child.Key, instance);
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

    private Task<Guid> DeliverAsync(Account account, string amount = "450,00 CZK", string? vs = "2026000123") =>
        P.DeliverAsync(account, ParsingHarness.Incoming(account.Bank, amount, vs));

    private async Task<JsonNode> GetAsync(Account account, string path) =>
        await P.Client(account.Key).GetAsync(path).ExpectAsync(HttpStatusCode.OK);

    private Task<JsonNode> RequestAsync(Account account, JsonNode request) => GetAsync(account, $"{Requests}/{request.Str("id")}");

    private async Task<JsonNode> OnlyTransactionAsync(Account account, string? query = null) =>
        Assert.Single((await GetAsync(account, $"{Transactions}{query}")).Items())!;

    private static Task<HttpResponseMessage> CommandAsync(HttpClient client, string path, object body, string? key = null)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (key != "")
        {
            message.Headers.Add(MatchingEndpoints.IdempotencyKeyHeader, key ?? Guid.NewGuid().ToString());
        }

        return client.SendAsync(message);
    }

    private static Task<HttpResponseMessage> AllocateAsync(HttpClient client, JsonNode transaction, JsonNode request, string reason, string? key = null) =>
        CommandAsync(client, $"{Transactions}/{transaction.Str("id")}/allocations", new
        {
            payment_request_id = request.Str("id"),
            expected_transaction_version = transaction["version"]!.GetValue<int>(),
            expected_request_version = request["version"]!.GetValue<int>(),
            reason,
        }, key);

    private static object Versions(JsonNode detail) => new Dictionary<string, int>(detail["transactions"]!.AsArray()
        .Select(t => KeyValuePair.Create(t!.Str("id"), t!["version"]!.GetValue<int>())));

    private static (string Status, long Paid, long? Remaining, long Overpaid) Summary(JsonNode request) => (
        request.Str("status"), request["paid_amount"]!.GetValue<long>(), request["remaining_amount"]?.GetValue<long>(),
        request["overpaid_amount"]!.GetValue<long>());

    [Fact] // AC 1, 3, 11, 12
    public async Task An_exact_receipt_pays_its_request_once_and_leading_zeros_compare_equal()
    {
        var account = await NewAccountAsync();
        var client = P.Client(account.Key);
        var request = await CreateRequestAsync(account, "000123");
        var zero = await CreateRequestAsync(account, "0");

        await DeliverAsync(account, vs: "123");

        var paid = await RequestAsync(account, request);
        Assert.Equal(("paid", 45000L, (long?)0L, 0L), Summary(paid));
        var tx = await OnlyTransactionAsync(account);
        Assert.Equal(("allocated", "matched", request.Str("id")), (tx.Str("handling_state"), tx["matching"]!.Str("result"), tx["matching"]!.Str("payment_request_id")));
        var allocation = tx["matching"]!["allocation"]!;
        Assert.Equal(("automatic", "active", "live", 45000L), (allocation.Str("method"), allocation.Str("state"), allocation.Str("evidence"), allocation["amount"]!.GetValue<long>()));

        var changes = (await GetAsync(account, $"{Requests}/{request.Str("id")}/changes")).Items();
        var change = Assert.Single(changes)!;
        Assert.Equal((1, "automatic_allocation", "open", "paid"), (change["sequence"]!.GetValue<int>(), change.Str("cause"),
            change["before"]!.Str("status"), change["after"]!.Str("status")));
        Assert.Equal((tx.Str("id"), allocation.Str("id")), (change.Str("transaction_id"), change.Str("allocation_id")));

        // A paid request is read-only and no longer hands out its original full-amount QR.
        await client.GetAsync($"{Requests}/{request.Str("id")}/payment-instructions").ExpectErrorAsync(HttpStatusCode.Conflict, "payment_instructions_not_available");
        await client.GetAsync($"{Requests}/{request.Str("id")}/qr.png").ExpectErrorAsync(HttpStatusCode.Conflict, "payment_instructions_not_available");
        var cancel = new HttpRequestMessage(HttpMethod.Post, $"{Requests}/{request.Str("id")}/cancel") { Content = JsonContent.Create(new { reason = "x" }) };
        cancel.Headers.IfMatch.Add(new System.Net.Http.Headers.EntityTagHeaderValue($"\"{paid["version"]}\""));
        await client.SendAsync(cancel).ExpectErrorAsync(HttpStatusCode.Conflict, "payment_request_has_allocations");

        // Retries, the sweep, and a new process change nothing.
        await client.PostAsync($"{Transactions}/{tx.Str("id")}/match", null).ExpectAsync(HttpStatusCode.Accepted);
        await P.RunJobsAsync();
        await P.WithServicesAsync(s => s.GetRequiredService<MatchingScheduling>().SweepAsync());
        await P.RunJobsAsync();
        Assert.Single((await GetAsync(account, $"{Requests}/{request.Str("id")}/changes")).Items());
        Assert.Single((await GetAsync(account, $"{Requests}/{request.Str("id")}/allocations")).Items());

        // A receipt without a symbol never matches the symbol 0.
        await DeliverAsync(account, vs: null);
        var unmatched = await OnlyTransactionAsync(account, "?handling_state=unallocated");
        Assert.Equal("missing_variable_symbol", unmatched["matching"]!.Str("result"));
        Assert.Equal("open", (await RequestAsync(account, zero)).Str("status"));
    }

    [Fact] // AC 3
    public async Task A_receipt_that_arrives_before_its_request_is_matched_when_the_request_is_created()
    {
        var account = await NewAccountAsync();
        await DeliverAsync(account, vs: "777");
        Assert.Equal("request_not_found", (await OnlyTransactionAsync(account))["matching"]!.Str("result"));

        var request = await CreateRequestAsync(account, "777");

        Assert.Equal("paid", (await RequestAsync(account, request)).Str("status"));
        Assert.Equal("allocated", (await OnlyTransactionAsync(account)).Str("handling_state"));
    }

    [Fact] // AC 4, 6, 10, 11
    public async Task Partial_payments_top_ups_and_overpayments_are_allocated_by_hand_and_can_be_revoked()
    {
        var account = await NewAccountAsync();
        var client = P.Client(account.Key);
        var request = await CreateRequestAsync(account, "555", dueAt: "2026-01-01T00:00:00Z");

        await DeliverAsync(account, "200,00 CZK", "555");
        var first = await OnlyTransactionAsync(account);
        Assert.Equal(("unallocated", "amount_mismatch", request.Str("id")),
            (first.Str("handling_state"), first["matching"]!.Str("result"), first["matching"]!.Str("payment_request_id")));
        Assert.Equal("open", (await RequestAsync(account, request)).Str("status"));

        var candidates = await GetAsync(account, $"{Transactions}/{first.Str("id")}/matching-candidates");
        var candidate = Assert.Single(candidates["candidates"]!.AsArray())!;
        Assert.Equal(["variable_symbol_match", "amount_mismatch"], candidate["reasons"]!.AsArray().Select(r => r!.GetValue<string>()));
        Assert.Equal(("partially_paid", 25000L), (candidate["proposed"]!.Str("status"), candidate["proposed"]!["remaining_amount"]!.GetValue<long>()));

        // Commands need a key and the versions the caller saw.
        await AllocateAsync(client, first, request, "Paid in two parts", key: "").ExpectErrorAsync(HttpStatusCode.BadRequest, "idempotency_key_required");
        await CommandAsync(client, $"{Transactions}/{first.Str("id")}/allocations", new { payment_request_id = request.Str("id"), reason = "x" })
            .ExpectErrorAsync(HttpStatusCode.PreconditionRequired, "precondition_required");
        await CommandAsync(client, $"{Transactions}/{first.Str("id")}/allocations", new
        {
            payment_request_id = request.Str("id"), expected_transaction_version = 99, expected_request_version = 1, reason = "x",
        }).ExpectErrorAsync(HttpStatusCode.PreconditionFailed, "precondition_failed");
        await AllocateAsync(client, first, request, "  ").ExpectErrorAsync(HttpStatusCode.BadRequest, "invalid_reason");

        var key = Guid.NewGuid().ToString();
        var allocated = await AllocateAsync(client, first, request, "Paid in two parts", key).ExpectAsync(HttpStatusCode.Created);
        Assert.Equal(("partially_paid", 20000L, (long?)25000L, 0L), Summary(allocated["payment_request"]!));
        Assert.True(allocated["payment_request"]!["is_overdue"]!.GetValue<bool>());
        Assert.Equal(("manual", "Paid in two parts"), (allocated["allocation"]!.Str("method"), allocated["allocation"]!.Str("reason")));

        // The same command again returns the original outcome; the same key for something else conflicts.
        var replay = await AllocateAsync(client, first, request, "Paid in two parts", key);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal("true", Assert.Single(replay.Headers.GetValues("Idempotent-Replayed")));
        Assert.Equal(allocated.ToJsonString(), (await replay.ReadJsonAsync()).ToJsonString());
        await AllocateAsync(client, first, request, "Another reason", key).ExpectErrorAsync(HttpStatusCode.Conflict, "idempotency_key_conflict");
        Assert.Single((await GetAsync(account, $"{Requests}/{request.Str("id")}/allocations")).Items());

        // A partially paid request can still be edited; it has no fresh QR for the full amount.
        var partial = await RequestAsync(account, request);
        var patch = new HttpRequestMessage(HttpMethod.Patch, $"{Requests}/{request.Str("id")}") { Content = JsonContent.Create(new { description = "Two parts" }) };
        patch.Headers.IfMatch.Add(new System.Net.Http.Headers.EntityTagHeaderValue($"\"{partial["version"]}\""));
        await client.SendAsync(patch).ExpectAsync(HttpStatusCode.OK);
        await client.GetAsync($"{Requests}/{request.Str("id")}/payment-instructions").ExpectErrorAsync(HttpStatusCode.Conflict, "payment_instructions_not_available");

        // A top-up is never allocated automatically, even when it completes the amount.
        await DeliverAsync(account, "300,00 CZK", "555");
        var second = Assert.Single((await GetAsync(account, $"{Transactions}?handling_state=unallocated")).Items())!;
        Assert.Equal("request_already_paid", second["matching"]!.Str("result"));
        var over = await AllocateAsync(client, second, await RequestAsync(account, request), "Second part, 50 CZK too much").ExpectAsync(HttpStatusCode.Created);
        Assert.Equal(("overpaid", 50000L, (long?)0L, 5000L), Summary(over["payment_request"]!));
        Assert.False(over["payment_request"]!["is_overdue"]!.GetValue<bool>());

        // Revoking keeps history, recalculates, and blocks automatic rematching of the released receipt.
        var current = await RequestAsync(account, request);
        var firstNow = (await GetAsync(account, $"{Transactions}/{first.Str("id")}"));
        var revoked = await CommandAsync(client, $"/v1/tenant/allocations/{allocated["allocation"]!.Str("id")}/revoke", new
        {
            expected_allocation_version = 1,
            expected_transaction_version = firstNow["version"]!.GetValue<int>(),
            expected_request_version = current["version"]!.GetValue<int>(),
            reason = "Belongs to another booking",
        }).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(("revoked", "manual"), (revoked["allocation"]!.Str("state"), revoked["allocation"]!.Str("revocation_code")));
        Assert.Equal(("partially_paid", 30000L, (long?)15000L, 0L), Summary(revoked["payment_request"]!));
        Assert.Equal(("unallocated", true), (revoked["transaction"]!.Str("handling_state"), revoked["transaction"]!["matching"]!["auto_matching_blocked"]!.GetValue<bool>()));

        var history = (await GetAsync(account, $"{Requests}/{request.Str("id")}/changes")).Items();
        Assert.Equal([1, 2, 3], history.Select(c => c!["sequence"]!.GetValue<int>()));
        Assert.Equal(["manual_allocation", "manual_allocation", "allocation_revoked"], history.Select(c => c!.Str("cause")));
        var allocations = (await GetAsync(account, $"{Requests}/{request.Str("id")}/allocations")).Items();
        Assert.Equal(["revoked", "active"], allocations.Select(a => a!.Str("state")));

        // A receipt that would overflow the total is rejected without saving anything.
        await P.WithDbAsync(db => db.PaymentRequests.Where(r => r.Id == Guid.Parse(request.Str("id")))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.PaidAmount, long.MaxValue - 1).SetProperty(r => r.Status, Core.Payments.PaymentRequestStatus.Overpaid)));
        var released = await GetAsync(account, $"{Transactions}/{first.Str("id")}");
        await AllocateAsync(client, released, await RequestAsync(account, request), "Overflow").ExpectErrorAsync(HttpStatusCode.Conflict, "amount_overflow");
        Assert.Equal(2, (await GetAsync(account, $"{Requests}/{request.Str("id")}/allocations")).Items().Count);
    }

    [Fact] // AC 6
    public async Task Ignored_receipts_are_restored_to_manual_handling_and_matched_only_after_an_explicit_enable()
    {
        var account = await NewAccountAsync();
        var client = P.Client(account.Key);
        await DeliverAsync(account, vs: "999");
        var tx = await OnlyTransactionAsync(account);

        var ignored = await CommandAsync(client, $"{Transactions}/{tx.Str("id")}/ignore", new { expected_version = tx["version"]!.GetValue<int>(), reason = "Private transfer" })
            .ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(("ignored", "Private transfer"), (ignored.Str("handling_state"), ignored["matching"]!.Str("handling_reason")));

        var request = await CreateRequestAsync(account, "999");
        Assert.Equal("open", (await RequestAsync(account, request)).Str("status"));
        await AllocateAsync(client, ignored, request, "x").ExpectErrorAsync(HttpStatusCode.Conflict, "transaction_ignored");

        var restored = await CommandAsync(client, $"{Transactions}/{tx.Str("id")}/restore", new { expected_version = ignored["version"]!.GetValue<int>(), reason = "It was a lesson" })
            .ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(("unallocated", "restored"), (restored.Str("handling_state"), restored["matching"]!.Str("auto_matching_block_reason")));
        await CommandAsync(client, $"{Transactions}/{tx.Str("id")}/restore", new { expected_version = restored["version"]!.GetValue<int>(), reason = "again" })
            .ExpectErrorAsync(HttpStatusCode.Conflict, "transaction_not_ignored");

        // Restoring alone does not match; enabling queues a fresh attempt.
        await P.RunJobsAsync();
        Assert.Equal("open", (await RequestAsync(account, request)).Str("status"));
        await CommandAsync(client, $"{Transactions}/{tx.Str("id")}/enable-auto-matching", new { expected_version = restored["version"]!.GetValue<int>(), reason = "Match it" })
            .ExpectAsync(HttpStatusCode.OK);
        await P.RunJobsAsync();
        Assert.Equal("paid", (await RequestAsync(account, request)).Str("status"));
    }

    [Fact] // AC 5, 7, 9
    public async Task A_lookalike_receipt_revokes_the_allocation_atomically_and_the_review_decides()
    {
        var account = await NewAccountAsync();
        var client = P.Client(account.Key);
        var request = await CreateRequestAsync(account, "4242");
        await DeliverAsync(account, vs: "4242");
        Assert.Equal("paid", (await RequestAsync(account, request)).Str("status"));

        // The same amount, date, and symbol again, without a bank reference: maybe a copy, maybe a second payment.
        await DeliverAsync(account, vs: "4242");

        var reopened = await RequestAsync(account, request);
        Assert.Equal(("open", 0L, (long?)45000L, 0L), Summary(reopened));
        var reviewCase = Assert.Single((await GetAsync(account, $"{Cases}?state=open")).Items())!;
        Assert.Equal("possible_duplicate", reviewCase.Str("issue"));
        Assert.Equal([reviewCase.Str("id")], reopened["open_review_case_ids"]!.AsArray().Select(i => i!.GetValue<string>()));
        var changes = (await GetAsync(account, $"{Requests}/{request.Str("id")}/changes")).Items();
        Assert.Equal(["automatic_allocation", "transaction_review_required"], changes.Select(c => c!.Str("cause")));
        var revoked = Assert.Single((await GetAsync(account, $"{Requests}/{request.Str("id")}/allocations")).Items())!;
        Assert.Equal(("revoked", "transaction_review_required", "system"), (revoked.Str("state"), revoked.Str("revocation_code"), revoked.Str("revoked_by")));

        var detail = await GetAsync(account, $"{Cases}/{reviewCase.Str("id")}");
        var members = detail["transactions"]!.AsArray();
        Assert.Equal(2, members.Count);
        Assert.All(members, m => Assert.Equal("review_required", m!["matching"]!.Str("manual_block")));
        await AllocateAsync(client, members[0]!, reopened, "x").ExpectErrorAsync(HttpStatusCode.Conflict, "transaction_review_required");

        // Stale or incomplete versions are refused; a decision states the check performed.
        var resolve = $"{Cases}/{reviewCase.Str("id")}/resolve";
        await CommandAsync(client, resolve, new { decision = "mark_duplicate", reason = "x", expected_version = detail["case"]!["version"]!.GetValue<int>() })
            .ExpectErrorAsync(HttpStatusCode.PreconditionRequired, "precondition_required");
        await CommandAsync(client, resolve, new
        {
            decision = "mark_duplicate", reason = "x", expected_version = 99, expected_transaction_versions = Versions(detail),
            canonical_transaction_id = members[0]!.Str("id"),
        }).ExpectErrorAsync(HttpStatusCode.PreconditionFailed, "precondition_failed");

        var decided = await CommandAsync(client, resolve, new
        {
            decision = "mark_duplicate", reason = "Checked the statement in George: one payment",
            expected_version = detail["case"]!["version"]!.GetValue<int>(), expected_transaction_versions = Versions(detail),
            canonical_transaction_id = members[0]!.Str("id"),
        }).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(("resolved", "mark_duplicate"), (decided["case"]!.Str("state"), decided["case"]!.Str("resolution")));
        var canonical = decided["transactions"]!.AsArray().Single(t => t!.Str("id") == members[0]!.Str("id"))!;
        var copy = decided["transactions"]!.AsArray().Single(t => t!.Str("id") != members[0]!.Str("id"))!;
        Assert.Equal(("unallocated", true, (string?)null), (canonical.Str("handling_state"), canonical["matching"]!["manually_confirmed"]!.GetValue<bool>(),
            canonical["matching"]!["manual_block"]?.GetValue<string>()));
        Assert.Equal("manual_allocation_only", canonical["matching"]!.Str("automatic_block"));
        Assert.Equal(("duplicate", canonical.Str("id")), (copy.Str("handling_state"), copy["matching"]!.Str("canonical_transaction_id")));
        // The parser's verdict stays as it was; the decision is recorded separately.
        Assert.Equal("review_required", canonical.Str("ingestion_status"));

        await AllocateAsync(client, copy, reopened, "x").ExpectErrorAsync(HttpStatusCode.Conflict, "transaction_duplicate");
        var paid = await AllocateAsync(client, canonical, reopened, "One payment, confirmed in the statement").ExpectAsync(HttpStatusCode.Created);
        Assert.Equal("paid", paid["payment_request"]!.Str("status"));

        // Reopening removes eligibility again and revokes the allocation in the same step.
        var resolved = await GetAsync(account, $"{Cases}/{reviewCase.Str("id")}");
        var again = await CommandAsync(client, $"{Cases}/{reviewCase.Str("id")}/reopen", new
        {
            reason = "The statement shows two payments after all",
            expected_version = resolved["case"]!["version"]!.GetValue<int>(), expected_transaction_versions = Versions(resolved),
        }).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("open", again["case"]!.Str("state"));
        Assert.Equal("open", (await RequestAsync(account, request)).Str("status"));

        var distinct = await CommandAsync(client, resolve, new
        {
            decision = "confirm_distinct", reason = "Two payments in the statement",
            expected_version = again["case"]!["version"]!.GetValue<int>(), expected_transaction_versions = Versions(again),
        }).ExpectAsync(HttpStatusCode.OK);
        Assert.All(distinct["transactions"]!.AsArray(), t => Assert.True(t!["matching"]!["allocatable"]!.GetValue<bool>()));
        Assert.Equal(["mark_duplicate", "reopen", "confirm_distinct"], distinct["decisions"]!.AsArray().Select(d => d!.Str("action")));

        foreach (var t in distinct["transactions"]!.AsArray())
        {
            await AllocateAsync(client, t!, await RequestAsync(account, request), "Both payments are real").ExpectAsync(HttpStatusCode.Created);
        }

        Assert.Equal(("overpaid", 90000L, (long?)0L, 45000L), Summary(await RequestAsync(account, request)));
    }

    [Fact] // AC 8
    public async Task An_unverified_candidate_can_be_confirmed_by_a_person_without_changing_its_authentication()
    {
        var account = await NewAccountAsync();
        var client = P.Client(account.Key);
        var request = await CreateRequestAsync(account, "3131");
        var unverified = new SenderAuthentication("pass", "pass", "fail", "smtp_receiving_server", null);
        var messageId = await P.DeliverAsync(ParsingHarness.Delivery(account,
            SyntheticBankProfile.Notice(ParsingHarness.Incoming(account.Bank, vs: "3131")), authentication: unverified));

        Assert.Empty((await GetAsync(account, Transactions)).Items());
        var reviewCase = Assert.Single((await GetAsync(account, $"{Cases}?state=open")).Items())!;
        Assert.Equal(("unverified_candidate", messageId.ToString()), (reviewCase.Str("issue"), reviewCase.Str("inbound_message_id")));
        var detail = await GetAsync(account, $"{Cases}/{reviewCase.Str("id")}");
        Assert.Equal("untrusted", Assert.Single(detail["parse_runs"]!.AsArray())!.Str("trust_decision"));

        var confirmed = await CommandAsync(client, $"{Cases}/{reviewCase.Str("id")}/resolve", new
        {
            decision = "confirm_payment", reason = "The payment is on the account statement",
            expected_version = detail["case"]!["version"]!.GetValue<int>(), expected_transaction_versions = new { },
        }).ExpectAsync(HttpStatusCode.OK);
        var decision = Assert.Single(confirmed["decisions"]!.AsArray())!;
        var tx = Assert.Single(confirmed["transactions"]!.AsArray())!;
        Assert.Equal(tx.Str("id"), decision.Str("created_transaction_id"));
        Assert.Equal(("review_required", true, "manual_allocation_only"),
            (tx.Str("ingestion_status"), tx["matching"]!["manually_confirmed"]!.GetValue<bool>(), tx["matching"]!.Str("automatic_block")));
        // The run keeps the provider's result: a person's confirmation is not DMARC.
        Assert.Equal("fail", Assert.Single(confirmed["parse_runs"]!.AsArray())!["trust"]!["evidence"]!.Str("dmarc"));

        await P.RunJobsAsync();
        Assert.Equal("open", (await RequestAsync(account, request)).Str("status"));
        await AllocateAsync(client, tx, request, "Confirmed from the statement").ExpectAsync(HttpStatusCode.Created);
        Assert.Equal("paid", (await RequestAsync(account, request)).Str("status"));

        // Reprocessing the unchanged evidence neither reopens the case nor touches the allocation.
        await client.PostAsync($"{Tenant}/inbound-messages/{messageId}/reprocess", null).ExpectAsync(HttpStatusCode.Accepted);
        await P.RunJobsAsync();
        Assert.Empty((await GetAsync(account, $"{Cases}?state=open")).Items());
        Assert.Equal("paid", (await RequestAsync(account, request)).Str("status"));

        // A candidate for another account cannot be approved through a general bypass.
        var foreignAccount = BankConnectionTests.NewAccount();
        var other = await P.DeliverAsync(ParsingHarness.Delivery(account,
            SyntheticBankProfile.Notice(ParsingHarness.Incoming(foreignAccount, vs: "3232")), authentication: unverified));
        var wrong = (await GetAsync(account, $"{Cases}?state=open")).Items().Single(c => c!.Str("inbound_message_id") == other.ToString())!;
        await CommandAsync(client, $"{Cases}/{wrong.Str("id")}/resolve", new
        {
            decision = "confirm_payment", reason = "x", expected_version = wrong["version"]!.GetValue<int>(), expected_transaction_versions = new { },
        }).ExpectErrorAsync(HttpStatusCode.Conflict, "candidate_not_confirmable");
    }

    [Fact] // AC 9
    public async Task A_disputed_allocated_transaction_loses_its_allocation_and_needs_a_new_one()
    {
        var account = await NewAccountAsync();
        var client = P.Client(account.Key);
        var request = await CreateRequestAsync(account, "2026000123");
        var messageId = await DeliverAsync(account);
        Assert.Equal("paid", (await RequestAsync(account, request)).Str("status"));

        try
        {
            P.Profile.Version = "1.9.0";
            P.Profile.Adjust = fields => fields with { Amount = "540,00" };
            await client.PostAsync($"{Tenant}/inbound-messages/{messageId}/reprocess", null).ExpectAsync(HttpStatusCode.Accepted);
            await P.RunJobsAsync();

            Assert.Equal("open", (await RequestAsync(account, request)).Str("status"));
            var reviewCase = Assert.Single((await GetAsync(account, $"{Cases}?state=open")).Items())!;
            Assert.Equal("disputed", reviewCase.Str("issue"));
            var detail = await GetAsync(account, $"{Cases}/{reviewCase.Str("id")}");
            var kept = await CommandAsync(client, $"{Cases}/{reviewCase.Str("id")}/resolve", new
            {
                decision = "keep_existing", reason = "The statement shows 450 CZK",
                expected_version = detail["case"]!["version"]!.GetValue<int>(), expected_transaction_versions = Versions(detail),
            }).ExpectAsync(HttpStatusCode.OK);

            // Resolution does not revive the old allocation.
            Assert.Equal("open", (await RequestAsync(account, request)).Str("status"));
            var tx = Assert.Single(kept["transactions"]!.AsArray())!;
            await AllocateAsync(client, tx, await RequestAsync(account, request), "Kept after checking the statement").ExpectAsync(HttpStatusCode.Created);

            // The same contradiction again keeps the decision and the allocation.
            await client.PostAsync($"{Tenant}/inbound-messages/{messageId}/reprocess", null).ExpectAsync(HttpStatusCode.Accepted);
            await P.RunJobsAsync();
            Assert.Empty((await GetAsync(account, $"{Cases}?state=open")).Items());
            Assert.Equal("paid", (await RequestAsync(account, request)).Str("status"));
        }
        finally
        {
            P.Profile.Version = "1.0.0";
            P.Profile.Adjust = null;
        }
    }

    [Fact] // AC 2, 13
    public async Task Test_evidence_and_other_owners_never_pay_a_request()
    {
        var fixture = await NewAccountAsync(InboundHarness.InlineInstanceId);
        var request = await CreateRequestAsync(fixture, "2026000123");
        await DeliverAsync(fixture);
        var tx = await OnlyTransactionAsync(fixture);
        Assert.Equal(("fixture", "not_live_evidence"), (tx.Str("evidence"), tx["matching"]!.Str("result")));
        Assert.Equal("open", (await RequestAsync(fixture, request)).Str("status"));
        await AllocateAsync(P.Client(fixture.Key), tx, request, "x").ExpectErrorAsync(HttpStatusCode.Conflict, "not_live_evidence");

        // Another tenant sees nothing, a sibling's request is not a candidate, and the parent manages its child.
        var stranger = await NewAccountAsync();
        var strangerClient = P.Client(stranger.Key);
        await strangerClient.GetAsync($"{Transactions}/{tx.Str("id")}/matching-candidates").ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");
        await AllocateAsync(strangerClient, tx, request, "x").ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");
        await strangerClient.GetAsync($"{Requests}/{request.Str("id")}/changes").ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");

        var strangerRequest = await CreateRequestAsync(stranger, "2026000123");
        await DeliverAsync(stranger, "100,00 CZK");
        var strangerTx = await OnlyTransactionAsync(stranger);
        await AllocateAsync(P.Client(fixture.Key), strangerTx, strangerRequest, "x").ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");

        var parent = P.Client(fx.RootManagementKey);
        var asParent = await parent.GetAsync($"/v1/subtenants/{stranger.TenantId}/transactions/{strangerTx.Str("id")}/matching-candidates")
            .ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(strangerRequest.Str("id"), Assert.Single(asParent["candidates"]!.AsArray())!["payment_request"]!.Str("id"));
        var viaChildRoute = await CommandAsync(parent, $"/v1/subtenants/{stranger.TenantId}/transactions/{strangerTx.Str("id")}/allocations", new
        {
            payment_request_id = strangerRequest.Str("id"),
            expected_transaction_version = strangerTx["version"]!.GetValue<int>(),
            expected_request_version = strangerRequest["version"]!.GetValue<int>(),
            reason = "Parent confirmed a partial payment",
        }).ExpectAsync(HttpStatusCode.Created);
        Assert.Equal("partially_paid", viaChildRoute["payment_request"]!.Str("status"));
        Assert.StartsWith("api_key:", viaChildRoute["allocation"]!.Str("created_by"));
    }
}
