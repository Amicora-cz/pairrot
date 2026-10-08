using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Pairrot.Core.Payments;

namespace Pairrot.IntegrationTests;

[Collection(PairrotCollection.Name)]
public class PaymentRequestTests(PairrotFixture fx)
{
    private const string Own = "/v1/tenant/payment-requests";
    private const string OwnConnections = "/v1/tenant/bank-connections";

    private static string ChildPath(Guid childId) => $"/v1/subtenants/{childId}/payment-requests";

    private static string ChildConnections(Guid childId) => $"/v1/subtenants/{childId}/bank-connections";

    [Fact] // AC 1, 2, 7, 9
    public async Task A_created_request_is_listed_found_and_readable_after_a_restart_with_its_saved_instructions()
    {
        var tenant = await fx.CreateChildAsync();
        var client = fx.Client(tenant.Key);
        var account = BankConnectionTests.NewAccount();
        var connectionId = await CreateConnectionAsync(client, OwnConnections, account.AccountNumber);
        var externalId = PairrotFixture.Unique("booking");

        // Standalone QR generation stays stateless.
        await client.PostAsJsonAsync($"{OwnConnections}/{connectionId}/payment-qr", new { amount = 100, currency = "CZK", variable_symbol = "1" })
            .ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(0, await CountRequestsAsync(tenant.Id));
        var auditBefore = await CountAuditAsync(tenant.Id, AuditActionsPrefix);

        var response = await client.PostAsJsonAsync(Own, Body(connectionId, externalId, description: "Reservation", dueAt: "2026-10-15T18:00:00+02:00"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("\"1\"", response.Headers.ETag?.Tag);
        var created = await response.ReadJsonAsync();
        var id = created.Str("id");
        Assert.EndsWith($"{Own}/{id}", response.Headers.Location?.ToString());

        Assert.Equal(tenant.Id.ToString(), created.Str("tenant_id"));
        Assert.Equal(connectionId, created.Str("bank_connection_id"));
        Assert.Equal(externalId, created.Str("external_id"));
        Assert.Equal(account.AccountNumber, created.Str("account_number"));
        Assert.Equal(account.Iban, created.Str("iban"));
        Assert.Equal(45000, created["amount"]!.GetValue<long>());
        Assert.Equal("2026000123", created.Str("variable_symbol"));
        Assert.Equal("Yoga říjen", created.Str("message"));
        Assert.Equal("Reservation", created.Str("description"));
        Assert.Equal(DateTimeOffset.Parse("2026-10-15T16:00:00Z"), DateTimeOffset.Parse(created.Str("due_at")));
        Assert.Equal("open", created.Str("status"));
        Assert.Equal(0, created["paid_amount"]!.GetValue<long>());
        Assert.Equal(45000, created["remaining_amount"]!.GetValue<long>());
        Assert.Equal(1, created["version"]!.GetValue<int>());
        Assert.False(created["replayed"]!.GetValue<bool>());

        var payload = $"SPD*1.0*ACC:{account.Iban}*AM:450.00*CC:CZK*X-VS:2026000123*MSG:Yoga říjen";
        var instructions = created["payment_instructions"]!;
        Assert.Equal(payload, instructions.Str("payload"));
        Assert.Equal("SPAYD", instructions.Str("format"));
        Assert.Equal(PaymentRequestRules.QrGeneratorVersion, instructions.Str("generator_version"));

        // Listing, the external reference filter, and a fresh application host all see the same record.
        var list = await client.GetAsync(Own).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(id, Assert.Single(list["items"]!.AsArray())!.Str("id"));
        var found = await client.GetAsync($"{Own}?external_id={externalId}").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(id, Assert.Single(found["items"]!.AsArray())!.Str("id"));
        Assert.Empty((await client.GetAsync($"{Own}?external_id=other").ExpectAsync(HttpStatusCode.OK))["items"]!.AsArray());

        await using var restarted = fx.CreateFactory();
        var afterRestart = fx.Client(tenant.Key, restarted);
        var read = await afterRestart.GetAsync($"{Own}/{id}");
        Assert.Equal("\"1\"", read.Headers.ETag?.Tag);
        Assert.Equal(created.Str("created_at"), (await read.ReadJsonAsync()).Str("created_at"));

        // Saved instructions and the PNG decode to the original payment.
        var saved = await afterRestart.GetAsync($"{Own}/{id}/payment-instructions").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(payload, saved.Str("payload"));
        var png = await afterRestart.GetAsync($"{Own}/{id}/qr.png");
        Assert.Equal("image/png", png.Content.Headers.ContentType?.MediaType);
        Assert.Contains("no-store", png.Headers.CacheControl?.ToString());
        Assert.Equal(payload, PaymentQrTests.Decode(await png.Content.ReadAsByteArrayAsync()));

        // Creation is audited once; reads and rendering are not.
        Assert.Equal(auditBefore + 1, await CountAuditAsync(tenant.Id, AuditActionsPrefix));
    }

    [Fact] // AC 7
    public async Task Overdue_is_computed_and_does_not_change_the_state()
    {
        var tenant = await fx.CreateChildAsync();
        var client = fx.Client(tenant.Key);
        var connectionId = await CreateConnectionAsync(client, OwnConnections);

        var past = await client.PostAsJsonAsync(Own, Body(connectionId, "past", symbol: "1", dueAt: "2020-01-01T00:00:00Z"))
            .ExpectAsync(HttpStatusCode.Created);
        var future = await client.PostAsJsonAsync(Own, Body(connectionId, "future", symbol: "2", dueAt: "2099-01-01T00:00:00+01:00"))
            .ExpectAsync(HttpStatusCode.Created);
        await client.PostAsJsonAsync(Own, Body(connectionId, "undated", symbol: "3")).ExpectAsync(HttpStatusCode.Created);

        Assert.True(past["is_overdue"]!.GetValue<bool>());
        Assert.Equal("open", past.Str("status"));
        Assert.Equal(45000, past["remaining_amount"]!.GetValue<long>());
        Assert.False(future["is_overdue"]!.GetValue<bool>());

        var overdue = await client.GetAsync($"{Own}?overdue=true").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("past", Assert.Single(overdue["items"]!.AsArray())!.Str("external_id"));
        var notOverdue = await client.GetAsync($"{Own}?overdue=false").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(["undated", "future"], notOverdue["items"]!.AsArray().Select(i => i!.Str("external_id")));

        // An overdue request can still be paid.
        await client.GetAsync($"{Own}/{past.Str("id")}/payment-instructions").ExpectAsync(HttpStatusCode.OK);
    }

    [Fact] // AC 3
    public async Task Retries_return_one_record_and_changed_original_input_conflicts()
    {
        var tenant = await fx.CreateChildAsync();
        var client = fx.Client(tenant.Key);
        var connectionId = await CreateConnectionAsync(client, OwnConnections);
        var externalId = PairrotFixture.Unique("booking");

        // Eight concurrent identical creations: one record, the others are replays of it.
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            fx.Client(tenant.Key).PostAsJsonAsync(Own, Body(connectionId, externalId))));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var ids = new HashSet<string>();
        foreach (var r in responses)
        {
            ids.Add((await r.ReadJsonAsync()).Str("id"));
        }

        var id = Assert.Single(ids);
        Assert.Equal(1, await CountRequestsAsync(tenant.Id));
        Assert.Equal(1, await CountAuditAsync(tenant.Id, AuditActionsPrefix + "created"));

        // Explicit nulls and padding are the same input; replays carry the saved instructions.
        var replay = await client.PostRawAsync(Own, $$"""
            {"bank_connection_id":"{{connectionId}}","external_id":" {{externalId}} ","amount":45000,"currency":"CZK",
             "variable_symbol":"2026000123","message":" Yoga říjen ","description":null,"due_at":null}
            """).ExpectAsync(HttpStatusCode.OK);
        Assert.True(replay["replayed"]!.GetValue<bool>());
        Assert.Equal(id, replay.Str("id"));
        Assert.NotNull(replay["payment_instructions"]);

        // A deliberate change of any original field is a conflict, not a new record or an update.
        foreach (var changed in new object[]
        {
            Body(connectionId, externalId, amount: 45001),
            Body(connectionId, externalId, symbol: "2026000124"),
            Body(connectionId, externalId, message: "Yoga"),
            Body(connectionId, externalId, description: "Different"),
            Body(connectionId, externalId, dueAt: "2026-10-15T18:00:00+02:00"),
            Body(await CreateConnectionAsync(client, OwnConnections), externalId),
        })
        {
            await client.PostAsJsonAsync(Own, changed).ExpectErrorAsync(HttpStatusCode.Conflict, "external_id_conflict");
        }

        // After a metadata edit, retrying the original creation returns the edited record.
        await client.SendAsync(Patch($"{Own}/{id}", new { description = "Edited" }, "\"1\"")).ExpectAsync(HttpStatusCode.OK);
        var afterEdit = await client.PostAsJsonAsync(Own, Body(connectionId, externalId)).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Edited", afterEdit.Str("description"));
        Assert.Equal(2, afterEdit["version"]!.GetValue<int>());
        Assert.Equal(1, await CountRequestsAsync(tenant.Id));
    }

    [Fact] // AC 4
    public async Task Equivalent_variable_symbols_collide_on_one_account_but_not_across_accounts()
    {
        var tenant = await fx.CreateChildAsync();
        var client = fx.Client(tenant.Key);
        var first = await CreateConnectionAsync(client, OwnConnections);
        var second = await CreateConnectionAsync(client, OwnConnections);

        var saved = await client.PostAsJsonAsync(Own, Body(first, "a", symbol: "123")).ExpectAsync(HttpStatusCode.Created);
        await client.PostAsJsonAsync(Own, Body(first, "b", symbol: "000123")).ExpectErrorAsync(HttpStatusCode.Conflict, "variable_symbol_conflict");
        await client.PostAsJsonAsync(Own, Body(first, "c", symbol: "123")).ExpectErrorAsync(HttpStatusCode.Conflict, "variable_symbol_conflict");
        var other = await client.PostAsJsonAsync(Own, Body(second, "d", symbol: "000123")).ExpectAsync(HttpStatusCode.Created);
        Assert.Equal("000123", other.Str("variable_symbol"));

        // The filter compares keys too.
        var bySymbol = await client.GetAsync($"{Own}?variable_symbol=0123&bank_connection_id={first}").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(saved.Str("id"), Assert.Single(bySymbol["items"]!.AsArray())!.Str("id"));

        // Concurrent creations with one symbol and different references: exactly one wins.
        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            fx.Client(tenant.Key).PostAsJsonAsync(Own, Body(first, $"race_{i}", symbol: i % 2 == 0 ? "77" : "0077"))));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        foreach (var r in responses.Where(r => r.StatusCode != HttpStatusCode.Created))
        {
            await Task.FromResult(r).ExpectErrorAsync(HttpStatusCode.Conflict, "variable_symbol_conflict");
        }
    }

    [Fact] // AC 5, 8
    public async Task Cancellation_keeps_history_and_the_symbol_and_replay_does_not_reopen()
    {
        var tenant = await fx.CreateChildAsync();
        var client = fx.Client(tenant.Key);
        var connectionId = await CreateConnectionAsync(client, OwnConnections);
        var created = await client.PostAsJsonAsync(Own, Body(connectionId, "booking")).ExpectAsync(HttpStatusCode.Created);
        var path = $"{Own}/{created.Str("id")}";

        await client.PostAsJsonAsync($"{path}/cancel", new { reason = "Booking cancelled" })
            .ExpectErrorAsync(HttpStatusCode.PreconditionRequired, "precondition_required");
        await client.SendAsync(Post($"{path}/cancel", new { reason = "x" }, "\"7\"")).ExpectErrorAsync(HttpStatusCode.PreconditionFailed, "precondition_failed");

        // Two cancellations based on the same version: both succeed, one change is recorded.
        var cancels = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
            fx.Client(tenant.Key).SendAsync(Post($"{path}/cancel", new { reason = "  Booking cancelled  " }, "\"1\""))));
        Assert.All(cancels, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(1, await CountAuditAsync(tenant.Id, AuditActionsPrefix + "cancelled"));

        var cancelled = await client.GetAsync(path).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("cancelled", cancelled.Str("status"));
        Assert.Equal("Booking cancelled", cancelled.Str("cancellation_reason"));
        Assert.NotNull(cancelled["cancelled_at"]);
        Assert.Equal(0, cancelled["paid_amount"]!.GetValue<long>());
        Assert.Null(cancelled["remaining_amount"]);
        Assert.Equal(2, cancelled["version"]!.GetValue<int>());

        // Repeating is a no-op, also without a version.
        var again = await client.PostAsync($"{path}/cancel", null).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(2, again["version"]!.GetValue<int>());

        await client.SendAsync(Patch(path, new { description = "x" }, "\"2\"")).ExpectErrorAsync(HttpStatusCode.Conflict, "payment_request_cancelled");
        await client.GetAsync($"{path}/payment-instructions").ExpectErrorAsync(HttpStatusCode.Conflict, "payment_request_cancelled");
        var png = await client.GetAsync($"{path}/qr.png");
        Assert.Equal("application/json", png.Content.Headers.ContentType?.MediaType);
        await Task.FromResult(png).ExpectErrorAsync(HttpStatusCode.Conflict, "payment_request_cancelled");

        // Replay returns the cancelled record without instructions; the symbol stays reserved.
        var replay = await client.PostAsJsonAsync(Own, Body(connectionId, "booking")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("cancelled", replay.Str("status"));
        Assert.Null(replay["payment_instructions"]);
        await client.PostAsJsonAsync(Own, Body(connectionId, "rebooking")).ExpectErrorAsync(HttpStatusCode.Conflict, "variable_symbol_conflict");

        var history = await client.GetAsync($"{Own}?status=cancelled").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("booking", Assert.Single(history["items"]!.AsArray())!.Str("external_id"));
        Assert.Empty((await client.GetAsync($"{Own}?status=open").ExpectAsync(HttpStatusCode.OK))["items"]!.AsArray());
    }

    [Fact] // AC 8
    public async Task Edits_need_the_current_version_and_only_change_description_and_due_time()
    {
        var tenant = await fx.CreateChildAsync();
        var client = fx.Client(tenant.Key);
        var connectionId = await CreateConnectionAsync(client, OwnConnections);
        var created = await client.PostAsJsonAsync(Own, Body(connectionId, "booking", dueAt: "2026-10-15T18:00:00+02:00"))
            .ExpectAsync(HttpStatusCode.Created);
        var path = $"{Own}/{created.Str("id")}";

        await client.PatchJsonAsync(path, new { description = "x" }).ExpectErrorAsync(HttpStatusCode.PreconditionRequired, "precondition_required");
        await client.SendAsync(Patch(path, new { description = "x" }, "W/\"1\"")).ExpectErrorAsync(HttpStatusCode.PreconditionFailed, "precondition_failed");

        var edited = await client.SendAsync(Patch(path, new { description = "  Moved  ", due_at = "2026-10-20T10:00:00Z" }, "\"1\""));
        Assert.Equal("\"2\"", edited.Headers.ETag?.Tag);
        var body = await edited.ReadJsonAsync();
        Assert.Equal("Moved", body.Str("description"));
        Assert.Equal(DateTimeOffset.Parse("2026-10-20T10:00:00Z"), DateTimeOffset.Parse(body.Str("due_at")));

        // A second edit based on the old version is rejected without overwriting the first.
        await client.SendAsync(Patch(path, new { description = "Stale" }, "\"1\"")).ExpectErrorAsync(HttpStatusCode.PreconditionFailed, "precondition_failed");
        Assert.Equal("Moved", (await client.GetAsync(path).ExpectAsync(HttpStatusCode.OK)).Str("description"));

        // Concurrent edits from the same version: one wins.
        var racing = await Task.WhenAll(Enumerable.Range(0, 4).Select(i =>
            fx.Client(tenant.Key).SendAsync(Patch(path, new { description = $"Race {i}" }, "\"2\""))));
        Assert.Equal(1, racing.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.All(racing.Where(r => r.StatusCode != HttpStatusCode.OK), r => Assert.Equal(HttpStatusCode.PreconditionFailed, r.StatusCode));

        var cleared = await client.SendAsync(Patch(path, new { description = (string?)null, due_at = (string?)null }, "\"3\""))
            .ExpectAsync(HttpStatusCode.OK);
        Assert.Null(cleared["description"]);
        Assert.Null(cleared["due_at"]);

        // An unchanged edit does not create a version.
        var same = await client.SendAsync(Patch(path, new { description = (string?)null }, "\"4\"")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(4, same["version"]!.GetValue<int>());

        foreach (var (field, value) in new (string, object)[]
        {
            ("amount", 1), ("variable_symbol", "1"), ("message", "x"), ("bank_connection_id", Guid.NewGuid()), ("external_id", "x"),
            ("status", "open"), ("tenant_id", Guid.NewGuid()), ("currency", "CZK"), ("iban", "CZ00"),
        })
        {
            var request = Patch(path, new Dictionary<string, object> { [field] = value }, "\"4\"");
            var response = await client.SendAsync(request).ExpectAsync(HttpStatusCode.BadRequest);
            Assert.Equal("immutable_field", response["error"]!.Str("code"));
            Assert.Equal(field, response["error"]!.Str("field"));
        }

        await client.SendAsync(Patch(path, new { due_at = "2026-10-20T10:00:00" }, "\"4\"")).ExpectErrorAsync(HttpStatusCode.BadRequest, "invalid_due_at");
        await client.SendAsync(Patch(path, new { description = new string('x', 1001) }, "\"4\"")).ExpectErrorAsync(HttpStatusCode.BadRequest, "invalid_description");
    }

    [Fact] // AC 6
    public async Task Creation_input_is_validated_and_ownership_cannot_be_forged()
    {
        var tenant = await fx.CreateChildAsync();
        var stranger = await fx.CreateChildAsync();
        var client = fx.Client(tenant.Key);
        var connectionId = await CreateConnectionAsync(client, OwnConnections);
        var foreignConnection = await CreateConnectionAsync(fx.Client(stranger.Key), OwnConnections);

        await client.PostAsJsonAsync(Own, Body(foreignConnection, "x")).ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");
        await client.PostAsJsonAsync(Own, Body(Guid.NewGuid().ToString(), "x")).ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");

        foreach (var extra in new[] { "tenant_id", "iban", "account_number", "status", "payload", "id" })
        {
            await client.PostRawAsync(Own, $$"""{"bank_connection_id":"{{connectionId}}","external_id":"x","amount":1,"currency":"CZK","variable_symbol":"1","{{extra}}":"x"}""")
                .ExpectErrorAsync(HttpStatusCode.BadRequest, "invalid_request");
        }

        await ExpectFieldErrorAsync(client.PostAsJsonAsync(Own, new { external_id = "x", amount = 1, currency = "CZK", variable_symbol = "1" }),
            "bank_connection_id", "invalid_bank_connection");
        await ExpectFieldErrorAsync(client.PostAsJsonAsync(Own, Body("not-a-uuid", "x")), "bank_connection_id", "invalid_bank_connection");
        await ExpectFieldErrorAsync(client.PostAsJsonAsync(Own, Body(connectionId, " ")), "external_id", "invalid_external_id");
        await ExpectFieldErrorAsync(client.PostAsJsonAsync(Own, Body(connectionId, new string('x', 201))), "external_id", "invalid_external_id");
        await ExpectFieldErrorAsync(client.PostAsJsonAsync(Own, Body(connectionId, "x", amount: 0)), "amount", "invalid_amount");
        await ExpectFieldErrorAsync(client.PostAsJsonAsync(Own, Body(connectionId, "x", amount: 1_000_000_000)), "amount", "invalid_amount");
        await ExpectFieldErrorAsync(client.PostAsJsonAsync(Own, Body(connectionId, "x", currency: "EUR")), "currency", "unsupported_currency");
        await ExpectFieldErrorAsync(client.PostAsJsonAsync(Own, Body(connectionId, "x", symbol: "")), "variable_symbol", "invalid_variable_symbol");
        await ExpectFieldErrorAsync(client.PostAsJsonAsync(Own, Body(connectionId, "x", symbol: "12345678901")), "variable_symbol", "invalid_variable_symbol");
        await ExpectFieldErrorAsync(client.PostAsJsonAsync(Own, Body(connectionId, "x", message: "100% done")), "message", "invalid_message");
        await ExpectFieldErrorAsync(client.PostAsJsonAsync(Own, Body(connectionId, "x", message: new string('ž', 61))), "message", "message_too_long");
        await ExpectFieldErrorAsync(client.PostAsJsonAsync(Own, Body(connectionId, "x", dueAt: "2026-10-15")), "due_at", "invalid_due_at");
        Assert.Equal(0, await CountRequestsAsync(tenant.Id));

        // The database itself refuses a request that points at another tenant's account.
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => fx.WithDbAsync(async db =>
        {
            var connection = await db.BankConnections.SingleAsync(b => b.Id == Guid.Parse(foreignConnection));
            db.PaymentRequests.Add(new PaymentRequest
            {
                Id = Guid.CreateVersion7(), TenantId = tenant.Id, BankConnectionId = connection.Id, BankAccountId = connection.BankAccountId, ExternalId = "forged",
                AccountNumber = connection.AccountNumber, Iban = connection.Iban, Amount = 1, Currency = "CZK",
                VariableSymbol = "1", VariableSymbolKey = "1", CreationFingerprint = new string('0', 64),
                QrPayload = "SPD*1.0", QrFormat = "SPAYD", QrGeneratorVersion = PaymentRequestRules.QrGeneratorVersion,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }));
        Assert.Contains("fk_payment_requests_bank_connections", error.InnerException?.Message);
    }

    [Fact] // AC 6
    public async Task Parent_manages_a_direct_child_but_siblings_tenant_keys_and_other_roots_cannot()
    {
        var studio = await fx.CreateChildAsync();
        var sibling = await fx.CreateChildAsync();
        var management = fx.Client(fx.RootManagementKey);
        var connectionId = await CreateConnectionAsync(management, ChildConnections(studio.Id));

        var created = await management.PostAsJsonAsync(ChildPath(studio.Id), Body(connectionId, "booking")).ExpectAsync(HttpStatusCode.Created);
        var id = created.Str("id");
        Assert.Equal(studio.Id.ToString(), created.Str("tenant_id"));

        // The studio's own key sees the same record; a parent session works too.
        await fx.Client(studio.Key).GetAsync($"{Own}/{id}").ExpectAsync(HttpStatusCode.OK);
        var parentBrowser = await fx.SignInAsync(fx.RootId);
        await parentBrowser.GetAsync($"{ChildPath(studio.Id)}/{id}").ExpectAsync(HttpStatusCode.OK);

        // The parent's own scope, a sibling, and other roots see nothing.
        await fx.Client(fx.RootTenantKey).GetAsync($"{Own}/{id}").ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");
        await management.GetAsync($"{ChildPath(sibling.Id)}/{id}").ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");
        await fx.Client(sibling.Key).GetAsync($"{Own}/{id}").ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");
        await fx.Client(sibling.Key).GetAsync($"{ChildPath(studio.Id)}/{id}").ExpectErrorAsync(HttpStatusCode.Forbidden, "forbidden");
        await fx.Client(fx.RootTenantKey).GetAsync($"{ChildPath(studio.Id)}/{id}").ExpectErrorAsync(HttpStatusCode.Forbidden, "forbidden");
        var otherRoot = await fx.SetupRootAsync(PairrotFixture.Unique("root"));
        await fx.Client(otherRoot.ManagementKey!.Secret).GetAsync($"{ChildPath(studio.Id)}/{id}").ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");
        await fx.Client(otherRoot.ManagementKey!.Secret).PostAsJsonAsync(ChildPath(studio.Id), Body(connectionId, "x", symbol: "9"))
            .ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");

        // A suspended child is history for its parent: readable, but not changeable or payable.
        await management.PatchJsonAsync($"/v1/subtenants/{studio.Id}", new { status = "suspended" }).ExpectAsync(HttpStatusCode.OK);
        await management.GetAsync($"{ChildPath(studio.Id)}/{id}").ExpectAsync(HttpStatusCode.OK);
        await management.GetAsync(ChildPath(studio.Id)).ExpectAsync(HttpStatusCode.OK);
        await management.GetAsync($"{ChildPath(studio.Id)}/{id}/payment-instructions").ExpectErrorAsync(HttpStatusCode.Conflict, "tenant_not_active");
        await management.PostAsJsonAsync(ChildPath(studio.Id), Body(connectionId, "booking")).ExpectErrorAsync(HttpStatusCode.Conflict, "tenant_not_active");
        await management.SendAsync(Post($"{ChildPath(studio.Id)}/{id}/cancel", null, "\"1\"")).ExpectErrorAsync(HttpStatusCode.Conflict, "tenant_not_active");
        await fx.Client(studio.Key).GetAsync($"{Own}/{id}").ExpectErrorAsync(HttpStatusCode.Forbidden, "tenant_suspended");

        await management.DeleteAsync($"/v1/subtenants/{studio.Id}").ExpectAsync(HttpStatusCode.NoContent);
        await management.GetAsync($"{ChildPath(studio.Id)}/{id}").ExpectAsync(HttpStatusCode.OK);
        await management.GetAsync($"{ChildPath(studio.Id)}/{id}/qr.png").ExpectErrorAsync(HttpStatusCode.Conflict, "tenant_deleted");
    }

    [Fact] // AC 8
    public async Task Archiving_a_connection_keeps_its_requests_but_blocks_fresh_instructions()
    {
        var tenant = await fx.CreateChildAsync();
        var client = fx.Client(tenant.Key);
        var connectionId = await CreateConnectionAsync(client, OwnConnections);
        var created = await client.PostAsJsonAsync(Own, Body(connectionId, "booking")).ExpectAsync(HttpStatusCode.Created);
        var path = $"{Own}/{created.Str("id")}";

        // Archiving races with creations: each creation either happens before it or is refused.
        var creations = Enumerable.Range(0, 6).Select(i =>
            fx.Client(tenant.Key).PostAsJsonAsync(Own, Body(connectionId, $"race_{i}", symbol: $"{100 + i}"))).ToList();
        var archive = client.DeleteAsync($"{OwnConnections}/{connectionId}");
        var responses = await Task.WhenAll(creations);
        Assert.Equal(HttpStatusCode.NoContent, (await archive).StatusCode);
        var createdCount = 0;
        foreach (var response in responses)
        {
            if (response.StatusCode == HttpStatusCode.Created)
            {
                createdCount++;
                continue;
            }

            await Task.FromResult(response).ExpectErrorAsync(HttpStatusCode.Conflict, "bank_connection_archived");
        }

        Assert.Equal(1 + createdCount, await CountRequestsAsync(tenant.Id));

        var request = await client.GetAsync(path).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("open", request.Str("status"));
        await client.GetAsync($"{path}/payment-instructions").ExpectErrorAsync(HttpStatusCode.Conflict, "bank_connection_archived");
        await client.GetAsync($"{path}/qr.png").ExpectErrorAsync(HttpStatusCode.Conflict, "bank_connection_archived");
        await client.PostAsJsonAsync(Own, Body(connectionId, "late", symbol: "999")).ExpectErrorAsync(HttpStatusCode.Conflict, "bank_connection_archived");

        // A replay still finds the record, without instructions; the operator can still edit or cancel it.
        var replay = await client.PostAsJsonAsync(Own, Body(connectionId, "booking")).ExpectAsync(HttpStatusCode.OK);
        Assert.Null(replay["payment_instructions"]);
        await client.SendAsync(Patch(path, new { description = "Account closed" }, "\"1\"")).ExpectAsync(HttpStatusCode.OK);
        await client.SendAsync(Post($"{path}/cancel", null, "\"2\"")).ExpectAsync(HttpStatusCode.OK);
    }

    [Fact] // AC 6
    public async Task Browser_administrators_need_antiforgery_the_tenant_context_and_if_match()
    {
        var a = await fx.CreateChildAsync();
        var b = await fx.CreateChildAsync();
        var tabA = new BrowserClient(fx.Factory);
        await tabA.LoginAsync(await fx.CreateUserAsync(a.Id, b.Id));
        await tabA.SelectTenantAsync(a.Id);
        var connection = await tabA.PostAsync(OwnConnections, new { account_number = BankConnectionTests.NewAccount().AccountNumber })
            .ExpectAsync(HttpStatusCode.Created);
        var body = Body(connection.Str("id"), "booking");

        await tabA.SendAsync(HttpMethod.Post, Own, body, antiforgery: false).ExpectErrorAsync(HttpStatusCode.BadRequest, "invalid_antiforgery_token");
        var created = await tabA.PostAsync(Own, body).ExpectAsync(HttpStatusCode.Created);
        var path = $"{Own}/{created.Str("id")}";

        var patch = new HttpRequestMessage(HttpMethod.Patch, path) { Content = JsonContent.Create(new { description = "From the SPA" }) };
        patch.Headers.Add("X-XSRF-TOKEN", tabA.AntiforgeryToken);
        patch.Headers.Add("X-Pairrot-Tenant-Id", a.Id.ToString());
        patch.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        Assert.Equal(HttpStatusCode.OK, (await tabA.Http.SendAsync(patch)).StatusCode);

        var tabB = tabA.Clone(fx.Factory);
        await tabB.SelectTenantAsync(b.Id);
        await tabA.GetAsync(path).ExpectErrorAsync(HttpStatusCode.Conflict, "tenant_context_changed");
        await tabB.GetAsync(path).ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");
        Assert.Empty((await tabB.GetAsync(Own).ExpectAsync(HttpStatusCode.OK))["items"]!.AsArray());
    }

    private const string AuditActionsPrefix = "payment_request.";

    private static object Body(
        string connectionId,
        string externalId,
        long amount = 45000,
        string currency = "CZK",
        string? symbol = "2026000123",
        string? message = "Yoga říjen",
        string? description = null,
        string? dueAt = null) => new
        {
            bank_connection_id = connectionId,
            external_id = externalId,
            amount,
            currency,
            variable_symbol = symbol,
            message,
            description,
            due_at = dueAt,
        };

    private static HttpRequestMessage Patch(string url, object body, string ifMatch) => WithIfMatch(HttpMethod.Patch, url, body, ifMatch);

    private static HttpRequestMessage Post(string url, object? body, string ifMatch) => WithIfMatch(HttpMethod.Post, url, body, ifMatch);

    private static HttpRequestMessage WithIfMatch(HttpMethod method, string url, object? body, string ifMatch)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return request;
    }

    private static async Task<string> CreateConnectionAsync(HttpClient client, string path, string? accountNumber = null)
    {
        var connection = await client.PostAsJsonAsync(path, new { account_number = accountNumber ?? BankConnectionTests.NewAccount().AccountNumber })
            .ExpectAsync(HttpStatusCode.Created);
        return connection.Str("id");
    }

    private static async Task ExpectFieldErrorAsync(Task<HttpResponseMessage> request, string field, string code)
    {
        var body = await request.ExpectAsync(HttpStatusCode.BadRequest);
        Assert.Equal(field, body["error"]!.Str("field"));
        Assert.Equal(code, body["error"]!.Str("code"));
    }

    private async Task<int> CountRequestsAsync(Guid tenantId)
    {
        var count = 0;
        await fx.WithDbAsync(async db => count = await db.PaymentRequests.CountAsync(p => p.TenantId == tenantId));
        return count;
    }

    private async Task<int> CountAuditAsync(Guid tenantId, string actionPrefix)
    {
        var count = 0;
        await fx.WithDbAsync(async db =>
            count = await db.AuditEvents.CountAsync(e => e.TargetTenantId == tenantId && e.Action.StartsWith(actionPrefix)));
        return count;
    }
}
