using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Pairrot.Core.Payments;

namespace Pairrot.IntegrationTests;

/// <summary>
/// Feature 012: Pairrot assigns the variable symbol when creation omits it. Allocation is
/// serialized per bank connection in PostgreSQL, so the concurrency checks use separate clients
/// and therefore separate database contexts and connections.
/// </summary>
[Collection(PairrotCollection.Name)]
public class AutomaticVariableSymbolTests(PairrotFixture fx)
{
    private const string Own = "/v1/tenant/payment-requests";
    private const string OwnConnections = "/v1/tenant/bank-connections";

    [Fact] // AC 1, 4, 12
    public async Task An_omitted_or_null_symbol_is_assigned_saved_in_the_qr_and_replayed()
    {
        var tenant = await fx.CreateChildAsync();
        var client = fx.Client(tenant.Key);
        var account = BankConnectionTests.NewAccount();
        var connectionId = await CreateConnectionAsync(client, account.AccountNumber);

        // Standalone QR generation stays stateless: no request, no allocator, no reserved symbol.
        await client.PostAsJsonAsync($"{OwnConnections}/{connectionId}/payment-qr", new { amount = 100, currency = "CZK" })
            .ExpectAsync(HttpStatusCode.OK);
        Assert.Null(await CursorAsync(connectionId));

        var created = await client.PostAsJsonAsync(Own, Body(connectionId, "booking_123")).ExpectAsync(HttpStatusCode.Created);
        var id = created.Str("id");
        Assert.Equal("1", created.Str("variable_symbol"));
        Assert.Equal("open", created.Str("status"));
        Assert.False(created["replayed"]!.GetValue<bool>());

        var payload = $"SPD*1.0*ACC:{account.Iban}*AM:450.00*CC:CZK*X-VS:1*MSG:Yoga October";
        Assert.Equal(payload, created["payment_instructions"]!.Str("payload"));
        Assert.Equal(payload, (await client.GetAsync($"{Own}/{id}/payment-instructions").ExpectAsync(HttpStatusCode.OK)).Str("payload"));
        var png = await client.GetAsync($"{Own}/{id}/qr.png");
        Assert.Equal(payload, PaymentQrTests.Decode(await png.Content.ReadAsByteArrayAsync()));
        Assert.Equal(2, await CursorAsync(connectionId));
        await fx.WithDbAsync(async db =>
        {
            var saved = await db.PaymentRequests.SingleAsync(p => p.Id == Guid.Parse(id));
            Assert.Equal(("1", "1", VariableSymbolSource.Automatic, payload), (saved.VariableSymbol, saved.VariableSymbolKey, saved.VariableSymbolSource, saved.QrPayload));
        });

        // Omission and explicit null are the same automatic intent; neither issues another symbol.
        var replay = await client.PostRawAsync(Own, $$"""
            {"bank_connection_id":"{{connectionId}}","external_id":"booking_123","amount":45000,"currency":"CZK",
             "variable_symbol":null,"message":"Yoga October"}
            """).ExpectAsync(HttpStatusCode.OK);
        Assert.True(replay["replayed"]!.GetValue<bool>());
        Assert.Equal((id, "1"), (replay.Str("id"), replay.Str("variable_symbol")));
        Assert.Equal(payload, replay["payment_instructions"]!.Str("payload"));
        Assert.Equal(2, await CursorAsync(connectionId));

        // The creation mode is part of the original input: copying the assigned symbol back is a change.
        await client.PostAsJsonAsync(Own, Body(connectionId, "booking_123", symbol: "1")).ExpectErrorAsync(HttpStatusCode.Conflict, "external_id_conflict");
        await client.PostAsJsonAsync(Own, Body(connectionId, "booking_123", amount: 45001)).ExpectErrorAsync(HttpStatusCode.Conflict, "external_id_conflict");
        await client.PostAsJsonAsync(Own, Body(await CreateConnectionAsync(client), "booking_123"))
            .ExpectErrorAsync(HttpStatusCode.Conflict, "external_id_conflict");

        await client.PostAsJsonAsync(Own, Body(connectionId, "supplied", symbol: "000123")).ExpectAsync(HttpStatusCode.Created);
        await client.PostAsJsonAsync(Own, Body(connectionId, "supplied")).ExpectErrorAsync(HttpStatusCode.Conflict, "external_id_conflict");
        await client.PostAsJsonAsync(Own, Body(connectionId, "supplied", symbol: "123")).ExpectErrorAsync(HttpStatusCode.Conflict, "external_id_conflict");
        await client.PostAsJsonAsync(Own, Body(connectionId, "supplied", symbol: "000123")).ExpectAsync(HttpStatusCode.OK);

        // An edit, then cancellation: the retry returns the current record, without new audit events or symbols.
        var audits = await CountAuditAsync(tenant.Id);
        var patch = new HttpRequestMessage(HttpMethod.Patch, $"{Own}/{id}") { Content = JsonContent.Create(new { description = "Edited" }) };
        patch.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        await client.SendAsync(patch).ExpectAsync(HttpStatusCode.OK);
        var cancel = new HttpRequestMessage(HttpMethod.Post, $"{Own}/{id}/cancel");
        cancel.Headers.TryAddWithoutValidation("If-Match", "\"2\"");
        await client.SendAsync(cancel).ExpectAsync(HttpStatusCode.OK);
        var afterCancel = await client.PostAsJsonAsync(Own, Body(connectionId, "booking_123")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(("cancelled", "Edited", "1"), (afterCancel.Str("status"), afterCancel.Str("description"), afterCancel.Str("variable_symbol")));
        Assert.Null(afterCancel["payment_instructions"]);
        Assert.Equal(audits + 2, await CountAuditAsync(tenant.Id));
        Assert.Equal(2, await CursorAsync(connectionId));
    }

    [Fact] // AC 2, 9
    public async Task Allocation_starts_at_1_skips_every_saved_symbol_and_each_connection_counts_on_its_own()
    {
        var tenant = await fx.CreateChildAsync();
        var client = fx.Client(tenant.Key);
        var connectionId = await CreateConnectionAsync(client);
        var other = await CreateConnectionAsync(client);

        await client.PostAsJsonAsync(Own, Body(connectionId, "one", symbol: "1")).ExpectAsync(HttpStatusCode.Created);
        await client.PostAsJsonAsync(Own, Body(connectionId, "two", symbol: "0002")).ExpectAsync(HttpStatusCode.Created);
        var three = await client.PostAsJsonAsync(Own, Body(connectionId, "three", symbol: "3")).ExpectAsync(HttpStatusCode.Created);
        var cancel = new HttpRequestMessage(HttpMethod.Post, $"{Own}/{three.Str("id")}/cancel");
        cancel.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        await client.SendAsync(cancel).ExpectAsync(HttpStatusCode.OK);

        // A supplied high symbol does not drag the cursor up to it.
        await client.PostAsJsonAsync(Own, Body(connectionId, "high", symbol: "9999999999")).ExpectAsync(HttpStatusCode.Created);
        Assert.Equal(1, await CursorAsync(connectionId));

        Assert.Equal("4", (await client.PostAsJsonAsync(Own, Body(connectionId, "auto_a")).ExpectAsync(HttpStatusCode.Created)).Str("variable_symbol"));
        await client.PostAsJsonAsync(Own, Body(connectionId, "six", symbol: "006")).ExpectAsync(HttpStatusCode.Created);
        Assert.Equal("5", (await client.PostAsJsonAsync(Own, Body(connectionId, "auto_b")).ExpectAsync(HttpStatusCode.Created)).Str("variable_symbol"));
        Assert.Equal("7", (await client.PostAsJsonAsync(Own, Body(connectionId, "auto_c")).ExpectAsync(HttpStatusCode.Created)).Str("variable_symbol"));

        // A taken generated symbol is a conflict for a supplied one, as any other symbol is.
        await client.PostAsJsonAsync(Own, Body(connectionId, "dup", symbol: "0005")).ExpectErrorAsync(HttpStatusCode.Conflict, "variable_symbol_conflict");

        // Another connection of the same tenant starts from 1, even beside a supplied maximum.
        await client.PostAsJsonAsync(Own, Body(other, "other_high", symbol: "9999999999")).ExpectAsync(HttpStatusCode.Created);
        Assert.Equal("1", (await client.PostAsJsonAsync(Own, Body(other, "other_auto")).ExpectAsync(HttpStatusCode.Created)).Str("variable_symbol"));

        // A restart keeps the cursor and the reservations.
        await using var restarted = fx.CreateFactory();
        var afterRestart = fx.Client(tenant.Key, restarted);
        Assert.Equal("8", (await afterRestart.PostAsJsonAsync(Own, Body(connectionId, "auto_d")).ExpectAsync(HttpStatusCode.Created)).Str("variable_symbol"));
        Assert.Equal(9, await CursorAsync(connectionId));
    }

    [Fact] // AC 3
    public async Task Invalid_symbols_are_rejected_without_allocating()
    {
        var tenant = await fx.CreateChildAsync();
        var client = fx.Client(tenant.Key);
        var connectionId = await CreateConnectionAsync(client);

        foreach (var symbol in new[] { "", " ", "12a", "12345678901", "-1" })
        {
            var body = await client.PostAsJsonAsync(Own, Body(connectionId, "x", symbol: symbol)).ExpectAsync(HttpStatusCode.BadRequest);
            Assert.Equal(("variable_symbol", "invalid_variable_symbol"), (body["error"]!.Str("field"), body["error"]!.Str("code")));
        }

        foreach (var json in new[] { "123", "true", "[]", "{}" })
        {
            await client.PostRawAsync(Own, $$"""{"bank_connection_id":"{{connectionId}}","external_id":"x","amount":1,"currency":"CZK","variable_symbol":{{json}}}""")
                .ExpectErrorAsync(HttpStatusCode.BadRequest, "invalid_request");
        }

        // An otherwise invalid automatic creation allocates nothing either.
        await client.PostAsJsonAsync(Own, Body(connectionId, "x", amount: 0)).ExpectErrorAsync(HttpStatusCode.BadRequest, "invalid_amount");

        Assert.Null(await CursorAsync(connectionId));
        Assert.Equal("1", (await client.PostAsJsonAsync(Own, Body(connectionId, "x")).ExpectAsync(HttpStatusCode.Created)).Str("variable_symbol"));
    }

    [Fact] // AC 6, 7
    public async Task Concurrent_creations_get_distinct_symbols_and_one_record_per_reference()
    {
        var tenant = await fx.CreateChildAsync();
        var connectionId = await CreateConnectionAsync(fx.Client(tenant.Key));

        // Concurrent first use: one allocator, ten distinct symbols, nothing skipped.
        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(i =>
            fx.Client(tenant.Key).PostAsJsonAsync(Own, Body(connectionId, $"race_{i}"))));
        var symbols = new List<long>();
        foreach (var response in responses)
        {
            symbols.Add(long.Parse((await Task.FromResult(response).ExpectAsync(HttpStatusCode.Created)).Str("variable_symbol")));
        }

        Assert.Equal(Enumerable.Range(1, 10).Select(i => (long)i), symbols.Order());
        Assert.Equal(11, await CursorAsync(connectionId));

        // One reference, many identical automatic calls: one record and one symbol.
        responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            fx.Client(tenant.Key).PostAsJsonAsync(Own, Body(connectionId, "same"))));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        var results = await Task.WhenAll(responses.Select(r => r.ReadJsonAsync()));
        Assert.Single(results.Select(r => r.Str("id")).Distinct());
        Assert.Equal("11", Assert.Single(results.Select(r => r.Str("variable_symbol")).Distinct()));
        Assert.Equal(12, await CursorAsync(connectionId));

        // Supplied and automatic creations compete for 12 and 13: no normalized key is claimed twice.
        responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => fx.Client(tenant.Key).PostAsJsonAsync(Own,
            Body(connectionId, $"mixed_{i}", symbol: (i % 4) switch { 0 => "12", 1 => "0013", _ => null }))));
        Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict, r.StatusCode.ToString()));
        await fx.WithDbAsync(async db =>
        {
            var keys = await db.PaymentRequests.Where(p => p.BankConnectionId == Guid.Parse(connectionId)).Select(p => p.VariableSymbolKey).ToListAsync();
            Assert.Equal(keys.Count, keys.Distinct().Count());
            Assert.Equal(11 + responses.Count(r => r.StatusCode == HttpStatusCode.Created), keys.Count);
        });

        // One reference on two connections: one creation, one input conflict, no allocator change on the loser.
        var other = await CreateConnectionAsync(fx.Client(tenant.Key));
        var before = await CursorAsync(connectionId);
        responses = await Task.WhenAll(
            fx.Client(tenant.Key).PostAsJsonAsync(Own, Body(connectionId, "split")),
            fx.Client(tenant.Key).PostAsJsonAsync(Own, Body(other, "split")));
        var winner = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        await Task.FromResult(Assert.Single(responses, r => r != winner)).ExpectErrorAsync(HttpStatusCode.Conflict, "external_id_conflict");
        var winnerConnection = (await winner.ReadJsonAsync()).Str("bank_connection_id");
        Assert.Equal(1, await CountRequestsAsync(tenant.Id, "split"));
        if (winnerConnection == other)
        {
            Assert.Equal(before, await CursorAsync(connectionId));
        }
        else
        {
            Assert.Equal(before + 1, await CursorAsync(connectionId));
            Assert.Null(await CursorAsync(other));
        }
    }

    [Fact] // AC 8
    public async Task A_failure_before_commit_keeps_nothing_and_a_lost_response_is_replayed()
    {
        var tenant = await fx.CreateChildAsync();
        var client = fx.Client(tenant.Key);
        var connectionId = await CreateConnectionAsync(client);
        await client.PostAsJsonAsync(Own, Body(connectionId, "first")).ExpectAsync(HttpStatusCode.Created);

        // The insert fails after the symbol was chosen and the cursor moved, inside the transaction.
        var failing = PairrotFixture.Unique("fail");
        var function = $"fail_{Guid.NewGuid():N}";
        // Test-generated identifiers only; DDL cannot take parameters.
        var createTrigger = $"""
            CREATE FUNCTION {function}() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.external_id = '{failing}' THEN RAISE EXCEPTION 'injected failure'; END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER {function} BEFORE INSERT ON payment_requests FOR EACH ROW EXECUTE FUNCTION {function}();
            """;
        var dropTrigger = $"DROP TRIGGER {function} ON payment_requests; DROP FUNCTION {function}();";
        await fx.WithDbAsync(db => db.Database.ExecuteSqlRawAsync(createTrigger));
        try
        {
            await client.PostAsJsonAsync(Own, Body(connectionId, failing)).ExpectErrorAsync(HttpStatusCode.InternalServerError, "internal_error");
        }
        finally
        {
            await fx.WithDbAsync(db => db.Database.ExecuteSqlRawAsync(dropTrigger));
        }

        Assert.Equal(0, await CountRequestsAsync(tenant.Id, failing));
        Assert.Equal(2, await CursorAsync(connectionId));

        // The retry gets the symbol the failed attempt did not keep; a second retry, as after a lost response, replays it.
        var created = await client.PostAsJsonAsync(Own, Body(connectionId, failing)).ExpectAsync(HttpStatusCode.Created);
        Assert.Equal("2", created.Str("variable_symbol"));
        var replay = await fx.Client(tenant.Key, fx.CreateFactory()).PostAsJsonAsync(Own, Body(connectionId, failing)).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal((created.Str("id"), "2"), (replay.Str("id"), replay.Str("variable_symbol")));
    }

    [Fact] // AC 10
    public async Task The_maximum_symbol_is_assigned_once_and_exhaustion_never_wraps()
    {
        var tenant = await fx.CreateChildAsync();
        var client = fx.Client(tenant.Key);
        var connectionId = await CreateConnectionAsync(client);
        var low = await client.PostAsJsonAsync(Own, Body(connectionId, "low")).ExpectAsync(HttpStatusCode.Created);
        await SetCursorAsync(connectionId, PaymentRequestRules.MaxAutomaticVariableSymbol);

        var max = await client.PostAsJsonAsync(Own, Body(connectionId, "max")).ExpectAsync(HttpStatusCode.Created);
        Assert.Equal("9999999999", max.Str("variable_symbol"));
        Assert.Equal(PaymentRequestRules.ExhaustedVariableSymbolCursor, await CursorAsync(connectionId));

        await client.PostAsJsonAsync(Own, Body(connectionId, "none")).ExpectErrorAsync(HttpStatusCode.Conflict, "variable_symbol_space_exhausted");
        Assert.Equal(0, await CountRequestsAsync(tenant.Id, "none"));
        Assert.Equal(PaymentRequestRules.ExhaustedVariableSymbolCursor, await CursorAsync(connectionId));

        // Replays and unused supplied symbols still work; the cursor is never reset.
        Assert.Equal(low.Str("id"), (await client.PostAsJsonAsync(Own, Body(connectionId, "low")).ExpectAsync(HttpStatusCode.OK)).Str("id"));
        Assert.Equal(max.Str("id"), (await client.PostAsJsonAsync(Own, Body(connectionId, "max")).ExpectAsync(HttpStatusCode.OK)).Str("id"));
        await client.PostAsJsonAsync(Own, Body(connectionId, "supplied", symbol: "77")).ExpectAsync(HttpStatusCode.Created);
        await client.PostAsJsonAsync(Own, Body(connectionId, "none")).ExpectErrorAsync(HttpStatusCode.Conflict, "variable_symbol_space_exhausted");

        // A supplied maximum at the last candidate leaves nothing either.
        var other = await CreateConnectionAsync(client);
        await client.PostAsJsonAsync(Own, Body(other, "other_max", symbol: "9999999999")).ExpectAsync(HttpStatusCode.Created);
        await client.PostAsJsonAsync(Own, Body(other, "other_low")).ExpectAsync(HttpStatusCode.Created);
        await SetCursorAsync(other, PaymentRequestRules.MaxAutomaticVariableSymbol);
        await client.PostAsJsonAsync(Own, Body(other, "other_none")).ExpectErrorAsync(HttpStatusCode.Conflict, "variable_symbol_space_exhausted");

        // The database refuses a cursor past the sentinel.
        await Assert.ThrowsAnyAsync<Exception>(() => SetCursorAsync(connectionId, PaymentRequestRules.ExhaustedVariableSymbolCursor + 1));
    }

    [Fact] // AC 4, 9
    public async Task Permissions_and_lifecycle_rules_still_apply_to_automatic_creation()
    {
        var studio = await fx.CreateChildAsync();
        var sibling = await fx.CreateChildAsync();
        var management = fx.Client(fx.RootManagementKey);
        var connectionResponse = await management.PostAsJsonAsync($"/v1/subtenants/{studio.Id}/bank-connections",
            new { account_number = BankConnectionTests.NewAccount().AccountNumber }).ExpectAsync(HttpStatusCode.Created);
        var connectionId = connectionResponse.Str("id");
        var childPath = $"/v1/subtenants/{studio.Id}/payment-requests";

        var created = await management.PostAsJsonAsync(childPath, Body(connectionId, "booking")).ExpectAsync(HttpStatusCode.Created);
        Assert.Equal(("1", studio.Id.ToString()), (created.Str("variable_symbol"), created.Str("tenant_id")));

        // Unauthorized callers reserve nothing on the studio's connection.
        await fx.Client(sibling.Key).PostAsJsonAsync(Own, Body(connectionId, "stolen")).ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");
        await fx.Client(sibling.Key).PostAsJsonAsync(childPath, Body(connectionId, "stolen")).ExpectErrorAsync(HttpStatusCode.Forbidden, "forbidden");
        await fx.Client(fx.RootTenantKey).PostAsJsonAsync(Own, Body(connectionId, "stolen")).ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");
        await management.PostAsJsonAsync($"/v1/subtenants/{sibling.Id}/payment-requests", Body(connectionId, "stolen"))
            .ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");
        Assert.Equal(2, await CursorAsync(connectionId));

        // The studio's own key replays the parent's creation.
        var replay = await fx.Client(studio.Key).PostAsJsonAsync(Own, Body(connectionId, "booking")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(created.Str("id"), replay.Str("id"));

        // An archived connection: replays find the record without fresh instructions; new requests are refused.
        await management.DeleteAsync($"/v1/subtenants/{studio.Id}/bank-connections/{connectionId}").ExpectAsync(HttpStatusCode.NoContent);
        var archived = await management.PostAsJsonAsync(childPath, Body(connectionId, "booking")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(("1", null), (archived.Str("variable_symbol"), archived["payment_instructions"]));
        await management.PostAsJsonAsync(childPath, Body(connectionId, "late")).ExpectErrorAsync(HttpStatusCode.Conflict, "bank_connection_archived");
        Assert.Equal(2, await CursorAsync(connectionId));

        // A suspended child cannot create.
        var otherConnection = await CreateConnectionAsync(fx.Client(studio.Key));
        await management.PatchJsonAsync($"/v1/subtenants/{studio.Id}", new { status = "suspended" }).ExpectAsync(HttpStatusCode.OK);
        await management.PostAsJsonAsync(childPath, Body(otherConnection, "suspended")).ExpectErrorAsync(HttpStatusCode.Conflict, "tenant_not_active");
        Assert.Null(await CursorAsync(otherConnection));
    }

    private static object Body(string connectionId, string externalId, long amount = 45000, string? symbol = null) => symbol is null
        ? new { bank_connection_id = connectionId, external_id = externalId, amount, currency = "CZK", message = "Yoga October" }
        : new { bank_connection_id = connectionId, external_id = externalId, amount, currency = "CZK", variable_symbol = symbol, message = "Yoga October" };

    private static async Task<string> CreateConnectionAsync(HttpClient client, string? accountNumber = null) =>
        (await client.PostAsJsonAsync(OwnConnections, new { account_number = accountNumber ?? BankConnectionTests.NewAccount().AccountNumber })
            .ExpectAsync(HttpStatusCode.Created)).Str("id");

    /// <summary>The connection's allocator cursor, or <c>null</c> before its first use.</summary>
    private async Task<long?> CursorAsync(string connectionId)
    {
        long? cursor = null;
        await fx.WithDbAsync(async db => cursor = await db.VariableSymbolAllocators
            .Where(a => a.BankConnectionId == Guid.Parse(connectionId)).Select(a => (long?)a.NextCandidate).SingleOrDefaultAsync());
        return cursor;
    }

    private Task SetCursorAsync(string connectionId, long cursor) => fx.WithDbAsync(db => db.VariableSymbolAllocators
        .Where(a => a.BankConnectionId == Guid.Parse(connectionId)).ExecuteUpdateAsync(s => s.SetProperty(a => a.NextCandidate, cursor)));

    private async Task<int> CountRequestsAsync(Guid tenantId, string externalId)
    {
        var count = 0;
        await fx.WithDbAsync(async db => count = await db.PaymentRequests.CountAsync(p => p.TenantId == tenantId && p.ExternalId == externalId));
        return count;
    }

    private async Task<int> CountAuditAsync(Guid tenantId)
    {
        var count = 0;
        await fx.WithDbAsync(async db => count = await db.AuditEvents.CountAsync(e => e.TargetTenantId == tenantId && e.Action.StartsWith("payment_request.")));
        return count;
    }
}
