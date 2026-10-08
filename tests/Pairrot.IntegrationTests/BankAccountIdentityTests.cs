using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pairrot.Api.Endpoints;
using Pairrot.Core.Banking;
using Pairrot.Core.Inbound;
using Pairrot.Core.Matching;
using Pairrot.Core.Tenants;
using Pairrot.Core.Transactions;
using Pairrot.Core.Webhooks;
using Pairrot.Infrastructure.Banking;
using Pairrot.Infrastructure.Matching;
using Pairrot.Infrastructure.Tenants;

namespace Pairrot.IntegrationTests;

/// <summary>
/// Feature 013 end to end with the synthetic test profile: an account is released by archiving or
/// tenant deletion and registered again; variable symbols, bank references, and lookalikes are
/// keyed by the physical account; another tenant's records are never visible or changed.
/// </summary>
[Collection(PairrotCollection.Name)]
public class BankAccountIdentityTests(PairrotFixture fx)
{
    private const string Tenant = "/v1/tenant";
    private const string Connections = Tenant + "/bank-connections";
    private const string Requests = Tenant + "/payment-requests";
    private const string Transactions = Tenant + "/transactions";
    private const string Cases = Tenant + "/review-cases";

    private ParsingHarness P => fx.Parsing;

    private async Task<JsonNode> CreateRequestAsync(Account account, string? vs, long amount = 45000)
    {
        var created = await P.Client(account.Key).PostAsJsonAsync(Requests, new
        {
            bank_connection_id = account.ConnectionId, external_id = $"lesson-{Guid.NewGuid():N}", amount, currency = "CZK",
            variable_symbol = vs,
        }).ExpectAsync(HttpStatusCode.Created);
        await P.RunJobsAsync();
        return created;
    }

    private Task<HttpResponseMessage> PostRequestAsync(Account account, string? vs) =>
        P.Client(account.Key).PostAsJsonAsync(Requests, new
        {
            bank_connection_id = account.ConnectionId, external_id = $"lesson-{Guid.NewGuid():N}", amount = 45000, currency = "CZK",
            variable_symbol = vs,
        });

    private Task ArchiveAsync(Account account) =>
        P.Client(account.Key).DeleteAsync($"{Connections}/{account.ConnectionId}").ExpectAsync(HttpStatusCode.NoContent);

    private Task<Guid> DeliverAsync(Account account, string amount = "450,00 CZK", string? vs = "2026000123", string? reference = null) =>
        P.DeliverAsync(account, ParsingHarness.Incoming(account.Bank, amount, vs, reference));

    private async Task<JsonNode> GetAsync(Account account, string path) =>
        await P.Client(account.Key).GetAsync(path).ExpectAsync(HttpStatusCode.OK);

    private static Task<HttpResponseMessage> CommandAsync(HttpClient client, string path, object body)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        message.Headers.Add(MatchingEndpoints.IdempotencyKeyHeader, Guid.NewGuid().ToString());
        return client.SendAsync(message);
    }

    private static object Versions(JsonNode detail) => new Dictionary<string, int>(detail["transactions"]!.AsArray()
        .Select(t => KeyValuePair.Create(t!.Str("id"), t!["version"]!.GetValue<int>())));

    private static Task<HttpResponseMessage> AllocateAsync(HttpClient client, JsonNode transaction, JsonNode request, string reason) =>
        CommandAsync(client, $"{Transactions}/{transaction.Str("id")}/allocations", new
        {
            payment_request_id = request.Str("id"),
            expected_transaction_version = transaction["version"]!.GetValue<int>(),
            expected_request_version = request["version"]!.GetValue<int>(),
            reason,
        });

    /// <summary>An operator decision through the review service, as <c>reviews resolve</c> runs it.</summary>
    private Task<ServiceResult<CommandOutcome>> OperatorResolveAsync(Guid caseId, ReviewAction decision, string? reasonCode) =>
        P.WithServicesAsync(async services =>
        {
            var reviews = services.GetRequiredService<ReviewCaseService>();
            var detail = (await reviews.GetAsOperatorAsync(caseId)).Value!;
            var versions = detail.Transactions.ToDictionary(t => t.Transaction.Id, t => t.Transaction.Version);
            var command = new CommandRequest(Guid.NewGuid().ToString(), "review_case.resolve",
                ManualCommands.Fingerprint("review_case.resolve", caseId, decision.ToString(), reasonCode), Actor.Administrator("operator:test"));
            return await reviews.ResolveAsOperatorAsync(caseId,
                new ResolveInput(decision, "Compared with the bank statement", detail.Case.Version, versions, null, reasonCode),
                command, d => new { case_id = d.Case.Id, state = d.Case.State.ToString() });
        });

    [Fact] // AC 1, 7, 9
    public async Task A_re_registered_account_pays_an_open_request_of_the_archived_registration()
    {
        var child = await fx.CreateChildAsync();
        var old = await P.CreateAccountAsync(child.Id, child.Key);
        var request = await CreateRequestAsync(old, "7001");
        var other = await CreateRequestAsync(old, "7002");
        await ArchiveAsync(old);

        var current = await P.CreateAccountAsync(child.Id, child.Key, bank: old.Bank);
        var oldConnection = await GetAsync(old, $"{Connections}/{old.ConnectionId}");
        var newConnection = await GetAsync(current, $"{Connections}/{current.ConnectionId}");
        Assert.Equal(oldConnection.Str("bank_account_id"), newConnection.Str("bank_account_id"));
        Assert.Equal(("archived", "user"), (oldConnection.Str("status"), oldConnection.Str("archived_reason")));
        Assert.Equal(oldConnection.Str("bank_account_id"), request.Str("bank_account_id"));

        // The payer uses the instructions issued earlier; the notification arrives at the new address.
        await DeliverAsync(current, vs: "7001");

        var paid = await GetAsync(old, $"{Requests}/{request.Str("id")}");
        Assert.Equal(("paid", 45000L), (paid.Str("status"), paid["paid_amount"]!.GetValue<long>()));
        var allocation = Assert.Single((await GetAsync(old, $"{Requests}/{request.Str("id")}/allocations")).Items())!;
        Assert.Equal((current.ConnectionId.ToString(), old.ConnectionId.ToString(), newConnection.Str("bank_account_id")),
            (allocation.Str("bank_connection_id"), allocation.Str("payment_request_bank_connection_id"), allocation.Str("bank_account_id")));
        var transaction = Assert.Single((await GetAsync(current, Transactions)).Items())!;
        Assert.Equal((current.ConnectionId.ToString(), newConnection.Str("bank_account_id")),
            (transaction.Str("bank_connection_id"), transaction.Str("bank_account_id")));

        // The event identifies the old request and its external ID, and adds the account.
        await fx.WithDbAsync(async db =>
        {
            var change = await db.PaymentChanges.SingleAsync(c => c.PaymentRequestId == Guid.Parse(request.Str("id")));
            var body = JsonNode.Parse(WebhookEvents.PaymentBody(change))!;
            Assert.Equal((request.Str("id"), request.Str("external_id"), newConnection.Str("bank_account_id"), old.ConnectionId.ToString(), 1),
                (body["data"]!.Str("payment_request_id"), body["data"]!.Str("external_id"), body["data"]!.Str("bank_account_id"),
                    body["data"]!.Str("bank_connection_id"), body["schema_version"]!.GetValue<int>()));
        });

        // New codes belong on the current registration.
        await P.Client(old.Key).GetAsync($"{Requests}/{other.Str("id")}/payment-instructions")
            .ExpectErrorAsync(HttpStatusCode.Conflict, "bank_connection_archived");

        // The database also refuses an allocation across accounts.
        var foreignAccount = await P.CreateAccountAsync(child.Id, child.Key);
        var foreignRequest = await CreateRequestAsync(foreignAccount, "7003");
        await DeliverAsync(current, vs: "7099");
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => fx.WithDbAsync(async db =>
        {
            var tx = await db.IncomingTransactions.SingleAsync(t => t.BankConnectionId == current.ConnectionId && t.VariableSymbol == "7099");
            db.PaymentAllocations.Add(new PaymentAllocation
            {
                Id = Guid.CreateVersion7(), TenantId = child.Id, BankConnectionId = tx.BankConnectionId,
                PaymentRequestBankConnectionId = foreignAccount.ConnectionId, BankAccountId = tx.BankAccountId,
                TransactionId = tx.Id, PaymentRequestId = Guid.Parse(foreignRequest.Str("id")), Amount = tx.Amount, Currency = "CZK",
                Method = AllocationMethod.Manual, Reason = "forged", Evidence = "live", CreatedBy = "test", CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }));
        Assert.Contains("fk_payment_allocations_bank_connections", error.InnerException?.Message);
    }

    [Fact] // AC 2, 5, 6, 10
    public async Task A_deleted_tenants_account_is_registered_again_without_any_access_to_its_history()
    {
        var first = await fx.CreateChildAsync();
        var previous = await P.CreateAccountAsync(first.Id, first.Key);
        var supplied = await CreateRequestAsync(previous, "5005");
        var automatic = await CreateRequestAsync(previous, null);
        await DeliverAsync(previous, vs: "5005", reference: "REF-1");
        await DeliverAsync(previous, amount: "333,00 CZK", vs: "9999");
        Assert.Equal("paid", (await GetAsync(previous, $"{Requests}/{supplied.Str("id")}")).Str("status"));
        var previousTransactions = await P.WithDbAsync(db => db.IncomingTransactions.AsNoTracking()
            .Where(t => t.TenantId == previous.TenantId).OrderBy(t => t.Id).ToListAsync());
        Assert.Equal(2, previousTransactions.Count);

        await fx.Client(fx.RootManagementKey).DeleteAsync($"/v1/subtenants/{first.Id}").ExpectAsync(HttpStatusCode.NoContent);
        var archived = await fx.Client(fx.RootManagementKey).GetAsync($"/v1/subtenants/{first.Id}/bank-connections/{previous.ConnectionId}")
            .ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(("archived", "tenant_deleted"), (archived.Str("status"), archived.Str("archived_reason")));

        var second = await fx.CreateChildAsync();
        var next = await P.CreateAccountAsync(second.Id, second.Key, bank: previous.Bank);
        var client = P.Client(next.Key);
        Assert.Empty((await GetAsync(next, Requests)).Items());
        Assert.Empty((await GetAsync(next, Transactions)).Items());
        await client.GetAsync($"{Requests}/{supplied.Str("id")}").ExpectAsync(HttpStatusCode.NotFound);

        // Symbols are unique on the account: automatic ones skip the old ones, supplied ones conflict.
        var generated = await CreateRequestAsync(next, null);
        Assert.NotEqual(automatic.Str("variable_symbol"), generated.Str("variable_symbol"));
        Assert.NotEqual("5005", generated.Str("variable_symbol"));
        await PostRequestAsync(next, "5005").ExpectErrorAsync(HttpStatusCode.Conflict, "variable_symbol_conflict");
        await PostRequestAsync(next, "0005005").ExpectErrorAsync(HttpStatusCode.Conflict, "variable_symbol_conflict");
        await PostRequestAsync(next, automatic.Str("variable_symbol")).ExpectErrorAsync(HttpStatusCode.Conflict, "variable_symbol_conflict");

        // A new movement carrying an old symbol is not the new tenant's request.
        await DeliverAsync(next, vs: "5005", reference: "REF-2");
        var unrelated = Assert.Single((await GetAsync(next, Transactions)).Items())!;
        Assert.Equal(("unallocated", "request_not_found"), (unrelated.Str("handling_state"), unrelated["matching"]!.Str("result")));

        // A notification of the old tenant's movement (same bank reference) creates nothing and opens a local-only case.
        var copy = await DeliverAsync(next, vs: "5005", reference: "REF-1");
        Assert.Single((await GetAsync(next, Transactions)).Items());
        var runs = await GetAsync(next, $"{Tenant}/inbound-messages/{copy}/parse-runs");
        var run = runs["runs"]!.AsArray()[^1]!;
        Assert.Equal(("review_required", "account_movement_conflict"), (run.Str("outcome"), run.Str("reason_code")));
        Assert.Null(run["transaction_id"]);
        var conflict = Assert.Single((await GetAsync(next, $"{Cases}?state=open")).Items())!;
        Assert.Equal(("account_movement_conflict", "bank_reference_conflict"), (conflict.Str("issue"), conflict.Str("reason_code")));
        var detailResponse = await client.GetAsync($"{Cases}/{conflict.Str("id")}");
        var detailText = await detailResponse.Content.ReadAsStringAsync();
        var detail = JsonNode.Parse(detailText)!;
        Assert.Empty(detail["transactions"]!.AsArray());
        foreach (var foreign in previousTransactions.Select(t => t.Id.ToString()).Append(first.Id.ToString()).Append(previous.ConnectionId.ToString()))
        {
            Assert.DoesNotContain(foreign, detailText);
        }

        // Only an operator decides; a proven reference conflict can only be rejected.
        await CommandAsync(client, $"{Cases}/{conflict.Str("id")}/resolve", new
        {
            decision = "reject", reason = "A copy", expected_version = detail["case"]!["version"]!.GetValue<int>(), expected_transaction_versions = Versions(detail),
        }).ExpectErrorAsync(HttpStatusCode.Conflict, "operator_decision_required");
        var caseId = Guid.Parse(conflict.Str("id"));
        Assert.Equal("decision_not_applicable", (await OperatorResolveAsync(caseId, ReviewAction.ConfirmDistinct, null)).Error?.Code);
        Assert.Equal("invalid_reason_code", (await OperatorResolveAsync(caseId, ReviewAction.Reject, null)).Error?.Code);
        var rejected = await OperatorResolveAsync(caseId, ReviewAction.Reject, ReviewCaseService.DuplicateOfForeignMovement);
        Assert.Null(rejected.Error);
        var decided = await GetAsync(next, $"{Cases}/{conflict.Str("id")}");
        Assert.Equal(("resolved", "reject"), (decided["case"]!.Str("state"), decided["case"]!.Str("resolution")));
        Assert.Equal(ReviewCaseService.DuplicateOfForeignMovement, decided["decisions"]!.AsArray()[^1]!.Str("reason_code"));

        // A lookalike without a reference becomes a local record that nobody can allocate until an operator confirms it.
        var target = await CreateRequestAsync(next, "9999", amount: 33300);
        Assert.Equal("open", (await GetAsync(next, $"{Requests}/{target.Str("id")}")).Str("status"));
        await DeliverAsync(next, amount: "333,00 CZK", vs: "9999");
        var lookalike = (await GetAsync(next, Transactions)).Items().Single(t => t!["bank_transaction_id"] is null)!;
        Assert.Equal(("review_required", "account_movement_conflict"), (lookalike.Str("ingestion_status"), lookalike["matching"]!.Str("manual_block")));
        Assert.Equal("open", (await GetAsync(next, $"{Requests}/{target.Str("id")}")).Str("status"));
        await AllocateAsync(client, lookalike, target, "x").ExpectErrorAsync(HttpStatusCode.Conflict, "account_movement_conflict");
        var lookalikeCase = Assert.Single((await GetAsync(next, $"{Cases}?state=open")).Items())!;
        Assert.Equal(("account_movement_conflict", "possible_duplicate"), (lookalikeCase.Str("issue"), lookalikeCase.Str("reason_code")));
        var lookalikeDetail = await GetAsync(next, $"{Cases}/{lookalikeCase.Str("id")}");
        Assert.Equal([lookalike.Str("id")], lookalikeDetail["transactions"]!.AsArray().Select(t => t!.Str("id")));
        await CommandAsync(client, $"{Cases}/{lookalikeCase.Str("id")}/resolve", new
        {
            decision = "confirm_payment", reason = "Mine", expected_version = lookalikeDetail["case"]!["version"]!.GetValue<int>(),
            expected_transaction_versions = Versions(lookalikeDetail),
        }).ExpectErrorAsync(HttpStatusCode.Conflict, "operator_decision_required");

        Assert.Null((await OperatorResolveAsync(Guid.Parse(lookalikeCase.Str("id")), ReviewAction.ConfirmDistinct, "distinct_movement")).Error);
        var confirmed = await GetAsync(next, $"{Transactions}/{lookalike.Str("id")}");
        Assert.Null(confirmed["matching"]!["manual_block"]);
        Assert.Equal("manual_allocation_only", confirmed["matching"]!.Str("automatic_block"));
        var allocated = await AllocateAsync(client, confirmed, await GetAsync(next, $"{Requests}/{target.Str("id")}"), "Checked the statement")
            .ExpectAsync(HttpStatusCode.Created);
        Assert.Equal("paid", allocated["payment_request"]!.Str("status"));

        // Reopening restores the block and revokes the allocation; it grants nothing.
        var resolved = await GetAsync(next, $"{Cases}/{lookalikeCase.Str("id")}");
        await CommandAsync(client, $"{Cases}/{lookalikeCase.Str("id")}/reopen", new
        {
            reason = "Second look", expected_version = resolved["case"]!["version"]!.GetValue<int>(), expected_transaction_versions = Versions(resolved),
        }).ExpectAsync(HttpStatusCode.OK);
        var reopened = await GetAsync(next, $"{Transactions}/{lookalike.Str("id")}");
        Assert.Equal(("unallocated", "account_movement_conflict"), (reopened.Str("handling_state"), reopened["matching"]!.Str("manual_block")));
        Assert.Equal("open", (await GetAsync(next, $"{Requests}/{target.Str("id")}")).Str("status"));

        // The old tenant's records were never changed.
        var after = await P.WithDbAsync(db => db.IncomingTransactions.AsNoTracking()
            .Where(t => t.TenantId == previous.TenantId).OrderBy(t => t.Id).ToListAsync());
        Assert.Equal(previousTransactions.Select(t => (t.Id, t.Version, t.Handling, t.IngestionStatus, t.CanonicalTransactionId)),
            after.Select(t => (t.Id, t.Version, t.Handling, t.IngestionStatus, t.CanonicalTransactionId)));
        Assert.Equal(1, await P.WithDbAsync(db => db.TransactionSources.CountAsync(s => s.TransactionId == previousTransactions[0].Id)));
    }

    [Fact] // AC 6, 10
    public async Task One_tenant_notified_through_two_registrations_keeps_one_movement_with_both_sources()
    {
        var child = await fx.CreateChildAsync();
        var old = await P.CreateAccountAsync(child.Id, child.Key);
        await DeliverAsync(old, vs: "6006", reference: "REF-6");
        await ArchiveAsync(old);
        var current = await P.CreateAccountAsync(child.Id, child.Key, bank: old.Bank);

        var copy = await DeliverAsync(current, vs: "6006", reference: "REF-6");
        var movement = Assert.Single((await GetAsync(current, Transactions)).Items())!;
        Assert.Equal(old.ConnectionId.ToString(), movement.Str("bank_connection_id"));
        var detail = await GetAsync(current, $"{Transactions}/{movement.Str("id")}");
        Assert.Equal([old.ConnectionId.ToString(), current.ConnectionId.ToString()],
            detail["sources"]!.AsArray().Select(s => s!.Str("bank_connection_id")));
        var run = (await GetAsync(current, $"{Tenant}/inbound-messages/{copy}/parse-runs"))["runs"]!.AsArray()[^1]!;
        Assert.Equal(("duplicate", movement.Str("id")), (run.Str("outcome"), run.Str("transaction_id")));

        // Different facts under the same reference dispute the movement under this tenant; no second movement.
        await DeliverAsync(current, amount: "460,00 CZK", vs: "6006", reference: "REF-6");
        var disputed = Assert.Single((await GetAsync(current, Transactions)).Items())!;
        Assert.Equal("disputed", disputed.Str("ingestion_status"));
        var dispute = Assert.Single((await GetAsync(current, $"{Cases}?state=open")).Items())!;
        Assert.Equal(("disputed", current.ConnectionId.ToString()), (dispute.Str("issue"), dispute.Str("bank_connection_id")));

        // Concurrent parsers of one reference on one account leave one movement and every source link.
        var fresh = await fx.CreateChildAsync();
        var account = await P.CreateAccountAsync(fresh.Id, fresh.Key);
        var deliveries = Enumerable.Range(0, 3).Select(_ => ParsingHarness.Delivery(account,
            Pairrot.Tests.Shared.SyntheticBankProfile.Notice(ParsingHarness.Incoming(account.Bank, vs: "1010", reference: "REF-10")))).ToList();
        foreach (var delivery in deliveries)
        {
            await P.AcceptAsync(delivery);
        }

        await Task.WhenAll(P.RunJobsAsync(), P.RunJobsAsync(), P.RunJobsAsync());
        await P.RunJobsAsync();
        var one = Assert.Single(await P.TransactionsAsync(account.ConnectionId));
        Assert.Equal(3, await P.WithDbAsync(db => db.TransactionSources.CountAsync(s => s.TransactionId == one.Id)));
    }

    [Fact] // AC 3, 4
    public async Task Restore_reactivates_a_registration_once_and_releases_messages_held_by_archiving()
    {
        var child = await fx.CreateChildAsync();
        var management = fx.Client(fx.RootManagementKey);
        var account = await P.CreateAccountAsync(child.Id, child.Key);
        await ArchiveAsync(account);

        // Mail still arrives at the archived connection's address and is held.
        var held = await DeliverAsync(account, vs: "3003");
        var message = await GetAsync(account, $"{Tenant}/inbound-messages/{held}");
        Assert.Equal("bank_connection_archived", message.Str("hold_reason"));
        Assert.Empty(await P.TransactionsAsync(account.ConnectionId));

        // The parent restores its child's connection; the held message is parsed, once.
        var restorePath = $"/v1/subtenants/{child.Id}/bank-connections/{account.ConnectionId}/restore";
        var restored = await P.Client(fx.RootManagementKey).PostAsync(restorePath, null).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("active", restored.Str("status"));
        Assert.Null(restored["archived_reason"]);
        Assert.Null(restored["archived_at"]);
        Assert.NotNull(restored["inbound_address"]);
        await P.RunJobsAsync();
        Assert.Null((await GetAsync(account, $"{Tenant}/inbound-messages/{held}"))["hold_reason"]);
        Assert.Single(await P.TransactionsAsync(account.ConnectionId));

        await P.Client(account.Key).PostAsync($"{Connections}/{account.ConnectionId}/restore", null).ExpectAsync(HttpStatusCode.OK);
        await P.RunJobsAsync();
        Assert.Single(await P.TransactionsAsync(account.ConnectionId));
        await fx.WithDbAsync(async db => Assert.Equal(1, await db.AuditEvents.CountAsync(e =>
            e.TargetTenantId == child.Id && e.Action == AuditActions.BankConnectionRestored)));

        // Restore is refused while another registration of the account is active, also the owner's own.
        await ArchiveAsync(account);
        var stranger = await fx.CreateChildAsync();
        var squatter = await P.Client(stranger.Key).PostAsJsonAsync(Connections, new { account_number = account.Bank.AccountNumber })
            .ExpectAsync(HttpStatusCode.Created);
        await P.Client(account.Key).PostAsync($"{Connections}/{account.ConnectionId}/restore", null)
            .ExpectErrorAsync(HttpStatusCode.Conflict, "account_unavailable");
        await P.Client(stranger.Key).DeleteAsync($"{Connections}/{squatter.Str("id")}").ExpectAsync(HttpStatusCode.NoContent);
        var again = await P.Client(account.Key).PostAsJsonAsync(Connections, new { account_number = account.Bank.AccountNumber })
            .ExpectAsync(HttpStatusCode.Created);
        await P.Client(account.Key).PostAsync($"{Connections}/{account.ConnectionId}/restore", null)
            .ExpectErrorAsync(HttpStatusCode.Conflict, "account_unavailable");

        // Racing creations and a restore leave exactly one active registration.
        await P.Client(account.Key).DeleteAsync($"{Connections}/{again.Str("id")}").ExpectAsync(HttpStatusCode.NoContent);
        var others = await Task.WhenAll(fx.CreateChildAsync(), fx.CreateChildAsync(), fx.CreateChildAsync());
        var responses = await Task.WhenAll(others
            .Select(o => P.Client(o.Key).PostAsJsonAsync(Connections, new { account_number = account.Bank.AccountNumber }))
            .Append(P.Client(account.Key).PostAsync($"{Connections}/{account.ConnectionId}/restore", null))
            .Append(management.PostAsync($"/v1/subtenants/{child.Id}/bank-connections/{account.ConnectionId}/restore", null)));
        Assert.All(responses, r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.OK, HttpStatusCode.Conflict }));
        foreach (var conflict in responses.Where(r => r.StatusCode == HttpStatusCode.Conflict))
        {
            Assert.Equal("account_unavailable", JsonNode.Parse(await conflict.Content.ReadAsStringAsync())!["error"]!.Str("code"));
        }

        var accountId = await P.WithDbAsync(db => db.BankConnections.Where(b => b.Id == account.ConnectionId).Select(b => b.BankAccountId).SingleAsync());
        Assert.Equal(1, await P.WithDbAsync(db => db.BankConnections.CountAsync(b => b.BankAccountId == accountId && b.Status == BankConnectionStatus.Active)));
    }

    [Fact] // Section 5: operator CLI services
    public async Task Operators_see_every_registration_and_can_release_a_squatted_account()
    {
        var owner = await fx.CreateChildAsync();
        var squatter = await fx.CreateChildAsync();
        var bank = BankConnectionTests.NewAccount();
        var first = await P.Client(owner.Key).PostAsJsonAsync(Connections, new { account_number = bank.AccountNumber }).ExpectAsync(HttpStatusCode.Created);
        await P.Client(owner.Key).DeleteAsync($"{Connections}/{first.Str("id")}").ExpectAsync(HttpStatusCode.NoContent);
        var squatted = await P.Client(squatter.Key).PostAsJsonAsync(Connections, new { iban = bank.Iban }).ExpectAsync(HttpStatusCode.Created);
        await P.Client(owner.Key).PostAsJsonAsync(Connections, new { account_number = bank.AccountNumber })
            .ExpectErrorAsync(HttpStatusCode.Conflict, "account_unavailable");

        var found = await P.WithServicesAsync(s => s.GetRequiredService<BankConnectionService>().FindRegistrationsAsync(bank.AccountNumber));
        Assert.Equal([(owner.Id, BankConnectionStatus.Archived), (squatter.Id, BankConnectionStatus.Active)],
            found.Value!.Registrations.Select(r => (r.Owner.Id, r.Connection.Status)));

        var released = await P.WithServicesAsync(s => s.GetRequiredService<BankConnectionService>()
            .ReleaseAsOperatorAsync(Guid.Parse(squatted.Str("id")), "Registered by the wrong studio", Actor.Administrator("admin-cli:test")));
        Assert.Equal((BankConnectionStatus.Archived, BankConnectionArchivedReason.Operator), (released.Value!.Status, released.Value.ArchivedReason));
        await fx.WithDbAsync(async db => Assert.True(await db.AuditEvents.AnyAsync(e =>
            e.TargetTenantId == squatter.Id && e.Action == AuditActions.BankConnectionArchived && e.ActorDescription == "admin-cli:test")));

        await P.Client(owner.Key).PostAsJsonAsync(Connections, new { account_number = bank.AccountNumber }).ExpectAsync(HttpStatusCode.Created);
    }
}
