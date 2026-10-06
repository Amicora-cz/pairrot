using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Pairrot.Core.Banking;
using Pairrot.Core.Tenants;

namespace Pairrot.IntegrationTests;

[Collection(PairrotCollection.Name)]
public class BankConnectionTests(PairrotFixture fx)
{
    private const string Own = "/v1/tenant/bank-connections";
    private const string AddressPattern = @"^p_[a-z2-7]{24}@in\.pairrot\.test$";

    private static string ChildPath(Guid childId) => $"/v1/subtenants/{childId}/bank-connections";

    [Fact] // AC 1, 5
    public async Task Root_and_instructor_administrators_each_save_their_own_connection()
    {
        var instructor = await fx.CreateChildAsync();
        var rootAccount = NewAccount();
        var instructorAccount = NewAccount();

        var rootBrowser = await fx.SignInAsync(fx.RootId);
        var instructorBrowser = await fx.SignInAsync(instructor.Id);

        var rootConnection = await rootBrowser.PostAsync(Own, new { account_number = rootAccount.AccountNumber, label = "Main account" })
            .ExpectAsync(HttpStatusCode.Created);
        var instructorConnection = await instructorBrowser.PostAsync(Own, new { iban = instructorAccount.Iban })
            .ExpectAsync(HttpStatusCode.Created);

        Assert.Equal(fx.RootId.ToString(), rootConnection.Str("tenant_id"));
        Assert.Equal("Main account", rootConnection.Str("label"));
        Assert.Equal(rootAccount.Iban, rootConnection.Str("iban"));
        Assert.Equal(instructor.Id.ToString(), instructorConnection.Str("tenant_id"));
        Assert.Equal(instructorAccount.AccountNumber, instructorConnection.Str("account_number"));

        foreach (var connection in new[] { rootConnection, instructorConnection })
        {
            Assert.Equal("CZK", connection.Str("currency"));
            Assert.Equal("active", connection.Str("status"));
            Assert.Equal("not_created", connection.Str("address_status"));
            Assert.Equal("stub", connection.Str("email_provider"));
            Assert.Null(connection["inbound_address"]);
            Assert.Null(connection["address_created_at"]);
        }

        // Each sees only its own connection.
        var instructorList = await instructorBrowser.GetAsync(Own).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(instructorConnection.Str("id"), Assert.Single(instructorList["items"]!.AsArray())!.Str("id"));
        await instructorBrowser.GetAsync($"{Own}/{rootConnection.Str("id")}").ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");

        // A tenant key works on the same endpoints.
        await fx.Client(instructor.Key).GetAsync($"{Own}/{instructorConnection.Str("id")}").ExpectAsync(HttpStatusCode.OK);
    }

    [Fact] // AC 2
    public async Task Domestic_and_iban_inputs_share_one_identity_and_invalid_details_are_rejected()
    {
        var tenant = await fx.CreateChildAsync();
        var client = fx.Client(tenant.Key);
        var account = NewAccount();

        await client.PostAsJsonAsync(Own, new { account_number = account.AccountNumber, iban = account.Iban, currency = "CZK" })
            .ExpectAsync(HttpStatusCode.Created);

        // The same account in IBAN form, printed with spaces, is the same identity.
        var printed = string.Join(' ', account.Iban.Chunk(4).Select(c => new string(c)));
        await client.PostAsJsonAsync(Own, new { iban = printed }).ExpectErrorAsync(HttpStatusCode.Conflict, "duplicate_bank_connection");
        await client.PostAsJsonAsync(Own, new { account_number = "000000-" + account.AccountNumber })
            .ExpectErrorAsync(HttpStatusCode.Conflict, "duplicate_bank_connection");

        var other = NewAccount();
        await ExpectFieldErrorAsync(client.PostAsJsonAsync(Own, new { account_number = BreakCheckDigit(other.AccountNumber) }),
            "invalid_account_number", "account_number");
        await ExpectFieldErrorAsync(client.PostAsJsonAsync(Own, new { iban = "CZ00" + other.Iban[4..] }), "invalid_iban", "iban");
        await ExpectFieldErrorAsync(client.PostAsJsonAsync(Own, new { account_number = other.AccountNumber, iban = account.Iban }),
            "account_mismatch", "iban");
        await ExpectFieldErrorAsync(client.PostAsJsonAsync(Own, new { iban = "SK3112000000198742637541" }), "unsupported_country", "iban");
        await ExpectFieldErrorAsync(client.PostAsJsonAsync(Own, new { account_number = other.AccountNumber, currency = "EUR" }),
            "unsupported_currency", "currency");
        await client.PostAsJsonAsync(Own, new { label = "no account" }).ExpectErrorAsync(HttpStatusCode.BadRequest, "invalid_account_number");
        await client.PostAsJsonAsync(Own, new { account_number = other.AccountNumber, tenant_id = fx.RootId })
            .ExpectErrorAsync(HttpStatusCode.BadRequest, "invalid_request");

        var list = await client.GetAsync(Own).ExpectAsync(HttpStatusCode.OK);
        Assert.Single(list["items"]!.AsArray());
    }

    [Fact] // AC 3
    public async Task Concurrent_duplicates_create_one_connection_and_foreign_conflicts_reveal_nothing()
    {
        var tenant = await fx.CreateChildAsync(email: "owner@example.com");
        var account = NewAccount();

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            fx.Client(tenant.Key).PostAsJsonAsync(Own, new { account_number = account.AccountNumber })));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.Created),
            r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        await fx.WithDbAsync(async db => Assert.Equal(1, await db.BankConnections.CountAsync(b => b.Iban == account.Iban)));

        // Another tenant gets a generic error without the owner's identity or details.
        var stranger = await fx.CreateChildAsync();
        var response = await fx.Client(stranger.Key).PostAsJsonAsync(Own, new { iban = account.Iban });
        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("account_unavailable", JsonNode.Parse(text)!["error"]!.Str("code"));
        Assert.DoesNotContain(tenant.Id.ToString(), text);
        Assert.DoesNotContain("owner@example.com", text);
        Assert.DoesNotContain("archived", text);
    }

    [Fact] // AC 4
    public async Task Parent_configures_a_direct_child_but_siblings_and_wrong_keys_cannot()
    {
        var studio = await fx.CreateChildAsync();
        var sibling = await fx.CreateChildAsync();
        var management = fx.Client(fx.RootManagementKey);

        var created = await management.PostAsJsonAsync(ChildPath(studio.Id), new { account_number = NewAccount().AccountNumber, label = "Studio" })
            .ExpectAsync(HttpStatusCode.Created);
        var id = created.Str("id");
        Assert.Equal(studio.Id.ToString(), created.Str("tenant_id"));

        // A parent administrator session can do the same, and the child sees it as its own.
        var parent = await fx.SignInAsync(fx.RootId);
        await parent.PatchAsync($"{ChildPath(studio.Id)}/{id}", new { label = "Studio main" }).ExpectAsync(HttpStatusCode.OK);
        var own = await fx.Client(studio.Key).GetAsync($"{Own}/{id}").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Studio main", own.Str("label"));

        // The connection is not reachable through another child's route or by a sibling.
        await management.GetAsync($"{ChildPath(sibling.Id)}/{id}").ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");
        await management.PostAsync($"{ChildPath(sibling.Id)}/{id}/inbound-address", null).ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");
        await fx.Client(sibling.Key).GetAsync($"{Own}/{id}").ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");
        await fx.Client(sibling.Key).DeleteAsync($"{Own}/{id}").ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");
        var siblingBrowser = await fx.SignInAsync(sibling.Id);
        await siblingBrowser.GetAsync(ChildPath(studio.Id)).ExpectErrorAsync(HttpStatusCode.Forbidden, "forbidden");

        // Wrong key purposes.
        await fx.Client(sibling.Key).GetAsync(ChildPath(studio.Id)).ExpectErrorAsync(HttpStatusCode.Forbidden, "forbidden");
        await fx.Client(fx.RootTenantKey).GetAsync(ChildPath(studio.Id)).ExpectErrorAsync(HttpStatusCode.Forbidden, "forbidden");
        await management.GetAsync(Own).ExpectErrorAsync(HttpStatusCode.Forbidden, "forbidden");

        // An unrelated root cannot reach nalekci.cz's children.
        var otherRoot = await fx.SetupRootAsync(PairrotFixture.Unique("root"));
        await fx.Client(otherRoot.ManagementKey!.Secret).GetAsync($"{ChildPath(studio.Id)}/{id}")
            .ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");
    }

    [Fact] // AC 5, 6, 9
    public async Task Address_is_assigned_once_even_for_concurrent_requests()
    {
        var studio = await fx.CreateChildAsync(email: "studio@example.com");
        var email = await fx.CreateUserAsync(studio.Id);
        var browser = new BrowserClient(fx.Factory);
        await browser.LoginAsync(email);

        var created = await browser.PostAsync(Own, new { account_number = NewAccount().AccountNumber }).ExpectAsync(HttpStatusCode.Created);
        var id = created.Str("id");

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => browser.PostAsync($"{Own}/{id}/inbound-address")));
        var addresses = new List<string>();
        foreach (var response in responses)
        {
            var body = await Task.FromResult(response).ExpectAsync(HttpStatusCode.OK);
            Assert.Equal("stub_created", body.Str("address_status"));
            Assert.Equal("stub", body.Str("email_provider"));
            Assert.NotNull(body["address_created_at"]);
            addresses.Add(body.Str("inbound_address"));
        }

        var address = Assert.Single(addresses.Distinct());
        Assert.Matches(AddressPattern, address);
        Assert.DoesNotContain(studio.Id.ToString("N")[..8], address);

        // A later retry returns the same address.
        var again = await browser.PostAsync($"{Own}/{id}/inbound-address").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(address, again.Str("inbound_address"));

        var history = await browser.GetAsync("/v1/tenant/audit-events").ExpectAsync(HttpStatusCode.OK);
        var assigned = history["items"]!.AsArray().Where(e => e!.Str("action") == AuditActions.BankConnectionAddressAssigned).ToList();
        var assignedEvent = Assert.Single(assigned)!;
        Assert.Equal(id, assignedEvent["details"]!.Str("connection_id"));
        Assert.Equal(email, assignedEvent["actor"]!.Str("email"));

        // The contact email and the login email are untouched.
        var config = await browser.GetAsync("/v1/tenant/configuration").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("studio@example.com", config.Str("contact_email"));
        var session = await browser.GetAsync("/v1/auth/session").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(email, session["user"]!.Str("email"));

        // A different connection gets a different address.
        var second = await browser.PostAsync(Own, new { account_number = NewAccount().AccountNumber }).ExpectAsync(HttpStatusCode.Created);
        var secondAddress = await browser.PostAsync($"{Own}/{second.Str("id")}/inbound-address").ExpectAsync(HttpStatusCode.OK);
        Assert.NotEqual(address, secondAddress.Str("inbound_address"));
    }

    [Fact] // AC 7
    public async Task Bank_details_and_address_survive_an_application_restart()
    {
        var studio = await fx.CreateChildAsync();
        var account = NewAccount();
        var created = await fx.Client(studio.Key).PostAsJsonAsync(Own, new { iban = account.Iban, label = "Kept" })
            .ExpectAsync(HttpStatusCode.Created);
        var id = created.Str("id");
        var assigned = await fx.Client(studio.Key).PostAsync($"{Own}/{id}/inbound-address", null).ExpectAsync(HttpStatusCode.OK);

        await using var restarted = fx.CreateFactory();
        var read = await fx.Client(studio.Key, restarted).GetAsync($"{Own}/{id}").ExpectAsync(HttpStatusCode.OK);

        Assert.Equal(account.Iban, read.Str("iban"));
        Assert.Equal(account.AccountNumber, read.Str("account_number"));
        Assert.Equal("Kept", read.Str("label"));
        Assert.Equal(assigned.Str("inbound_address"), read.Str("inbound_address"));
        Assert.Equal("stub_created", read.Str("address_status"));
    }

    [Fact] // AC 10
    public async Task Account_identity_and_owner_are_immutable_and_label_changes_are_audited()
    {
        var studio = await fx.CreateChildAsync();
        var browser = await fx.SignInAsync(studio.Id);
        var created = await browser.PostAsync(Own, new { account_number = NewAccount().AccountNumber }).ExpectAsync(HttpStatusCode.Created);
        var path = $"{Own}/{created.Str("id")}";

        await ExpectFieldErrorAsync(browser.PatchAsync(path, new { iban = NewAccount().Iban }), "immutable_field", "iban");
        await ExpectFieldErrorAsync(browser.PatchAsync(path, new { account_number = NewAccount().AccountNumber }), "immutable_field", "account_number");
        await ExpectFieldErrorAsync(browser.PatchAsync(path, new { tenant_id = fx.RootId }), "immutable_field", "tenant_id");
        await ExpectFieldErrorAsync(browser.PatchAsync(path, new { inbound_address = "me@example.com" }), "immutable_field", "inbound_address");
        await ExpectFieldErrorAsync(browser.PatchAsync(path, new { currency = "EUR", label = "x" }), "immutable_field", "currency");
        await browser.PatchAsync(path, new { owner = "x" }).ExpectErrorAsync(HttpStatusCode.BadRequest, "invalid_request");
        await ExpectFieldErrorAsync(browser.PatchAsync(path, new { label = new string('x', 201) }), "invalid_request", "label");

        var updated = await browser.PatchAsync(path, new { label = "Renamed" }).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Renamed", updated.Str("label"));
        Assert.Equal(created.Str("iban"), updated.Str("iban"));
        await browser.PatchAsync(path, new { label = "Renamed" }).ExpectAsync(HttpStatusCode.OK); // no change, no event

        var cleared = await browser.PatchAsync(path, new { label = (string?)null }).ExpectAsync(HttpStatusCode.OK);
        Assert.Null(cleared["label"]);

        var history = await browser.GetAsync("/v1/tenant/audit-events").ExpectAsync(HttpStatusCode.OK);
        var labelEvents = history["items"]!.AsArray().Where(e => e!.Str("action") == AuditActions.BankConnectionLabelChanged).ToList();
        Assert.Equal(2, labelEvents.Count);
        Assert.Equal("Renamed", labelEvents[^1]!["details"]!.Str("to"));
        Assert.Equal("user", labelEvents[^1]!["actor"]!.Str("type"));
        Assert.Contains(history["items"]!.AsArray(), e => e!.Str("action") == AuditActions.BankConnectionCreated);
    }

    [Fact] // AC 11
    public async Task Archiving_keeps_the_address_reserved_and_blocks_new_assignments()
    {
        var studio = await fx.CreateChildAsync();
        var stranger = await fx.CreateChildAsync();
        var client = fx.Client(studio.Key);
        var account = NewAccount();

        var withAddress = await client.PostAsJsonAsync(Own, new { account_number = account.AccountNumber }).ExpectAsync(HttpStatusCode.Created);
        var path = $"{Own}/{withAddress.Str("id")}";
        var address = (await client.PostAsync($"{path}/inbound-address", null).ExpectAsync(HttpStatusCode.OK)).Str("inbound_address");
        var withoutAddress = await client.PostAsJsonAsync(Own, new { account_number = NewAccount().AccountNumber }).ExpectAsync(HttpStatusCode.Created);
        var withoutPath = $"{Own}/{withoutAddress.Str("id")}";

        await client.DeleteAsync(path).ExpectAsync(HttpStatusCode.NoContent);
        await client.DeleteAsync(path).ExpectAsync(HttpStatusCode.NoContent);
        await client.DeleteAsync(withoutPath).ExpectAsync(HttpStatusCode.NoContent);

        var archived = await client.GetAsync(path).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("archived", archived.Str("status"));
        Assert.NotNull(archived["archived_at"]);
        Assert.Equal(address, archived.Str("inbound_address"));

        // The existing address is returned as history; no new address is assigned.
        var repeated = await client.PostAsync($"{path}/inbound-address", null).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(address, repeated.Str("inbound_address"));
        await client.PostAsync($"{withoutPath}/inbound-address", null).ExpectErrorAsync(HttpStatusCode.Conflict, "bank_connection_archived");
        await client.PatchJsonAsync(path, new { label = "late" }).ExpectErrorAsync(HttpStatusCode.Conflict, "bank_connection_archived");

        // Archived connections are listed only on request.
        var active = await client.GetAsync(Own).ExpectAsync(HttpStatusCode.OK);
        Assert.Empty(active["items"]!.AsArray());
        var all = await client.GetAsync($"{Own}?include_archived=true").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(2, all["items"]!.AsArray().Count);

        // The account stays reserved for this tenant and unavailable to others.
        await client.PostAsJsonAsync(Own, new { account_number = account.AccountNumber })
            .ExpectErrorAsync(HttpStatusCode.Conflict, "duplicate_bank_connection");
        await fx.Client(stranger.Key).PostAsJsonAsync(Own, new { account_number = account.AccountNumber })
            .ExpectErrorAsync(HttpStatusCode.Conflict, "account_unavailable");

        await fx.WithDbAsync(async db =>
        {
            Assert.Equal(1, await db.BankConnections.CountAsync(b => b.InboundAddress == address));
            var archiveEvents = await db.AuditEvents.CountAsync(e =>
                e.TargetTenantId == studio.Id && e.Action == AuditActions.BankConnectionArchived);
            Assert.Equal(2, archiveEvents); // one per connection, none for the repeat
        });
    }

    [Fact] // AC 12
    public async Task Suspended_and_deleted_tenants_can_be_inspected_but_not_changed()
    {
        var studio = await fx.CreateChildAsync();
        var management = fx.Client(fx.RootManagementKey);
        var created = await management.PostAsJsonAsync(ChildPath(studio.Id), new { account_number = NewAccount().AccountNumber })
            .ExpectAsync(HttpStatusCode.Created);
        var path = $"{ChildPath(studio.Id)}/{created.Str("id")}";

        await management.PatchJsonAsync($"/v1/subtenants/{studio.Id}", new { status = "suspended" }).ExpectAsync(HttpStatusCode.OK);
        await management.GetAsync(path).ExpectAsync(HttpStatusCode.OK);
        await management.GetAsync(ChildPath(studio.Id)).ExpectAsync(HttpStatusCode.OK);
        await management.PostAsync($"{path}/inbound-address", null).ExpectErrorAsync(HttpStatusCode.Conflict, "tenant_not_active");
        await management.PatchJsonAsync(path, new { label = "x" }).ExpectErrorAsync(HttpStatusCode.Conflict, "tenant_not_active");
        await management.PostAsJsonAsync(ChildPath(studio.Id), new { account_number = NewAccount().AccountNumber })
            .ExpectErrorAsync(HttpStatusCode.Conflict, "tenant_not_active");
        var ownResponse = await fx.Client(studio.Key).GetAsync(Own);
        Assert.Equal(HttpStatusCode.Forbidden, ownResponse.StatusCode);

        await management.DeleteAsync($"/v1/subtenants/{studio.Id}").ExpectAsync(HttpStatusCode.NoContent);
        var kept = await management.GetAsync(path).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("active", kept.Str("status"));
        await management.DeleteAsync(path).ExpectErrorAsync(HttpStatusCode.Conflict, "tenant_deleted");
        await management.PostAsync($"{path}/inbound-address", null).ExpectErrorAsync(HttpStatusCode.Conflict, "tenant_deleted");
    }

    [Fact] // AC 12
    public async Task Browser_requests_need_antiforgery_and_the_current_tenant_context()
    {
        var a = await fx.CreateChildAsync();
        var b = await fx.CreateChildAsync();
        var email = await fx.CreateUserAsync(a.Id, b.Id);
        var tabA = new BrowserClient(fx.Factory);
        await tabA.LoginAsync(email);
        await tabA.SelectTenantAsync(a.Id);

        await tabA.SendAsync(HttpMethod.Post, Own, new { account_number = NewAccount().AccountNumber }, antiforgery: false)
            .ExpectErrorAsync(HttpStatusCode.BadRequest, "invalid_antiforgery_token");
        var created = await tabA.PostAsync(Own, new { account_number = NewAccount().AccountNumber }).ExpectAsync(HttpStatusCode.Created);
        Assert.Equal(a.Id.ToString(), created.Str("tenant_id"));

        var tabB = tabA.Clone(fx.Factory);
        await tabB.SelectTenantAsync(b.Id);

        await tabA.PostAsync(Own, new { account_number = NewAccount().AccountNumber })
            .ExpectErrorAsync(HttpStatusCode.Conflict, "tenant_context_changed");
        await tabA.PostAsync($"{Own}/{created.Str("id")}/inbound-address")
            .ExpectErrorAsync(HttpStatusCode.Conflict, "tenant_context_changed");
        var bList = await tabB.GetAsync(Own).ExpectAsync(HttpStatusCode.OK);
        Assert.Empty(bList["items"]!.AsArray());

        // Signed out, nothing is reachable.
        await tabB.PostAsync("/v1/auth/logout").ExpectAsync(HttpStatusCode.NoContent);
        await tabB.GetAsync(Own).ExpectAsync(HttpStatusCode.Unauthorized);
    }

    private static async Task ExpectFieldErrorAsync(Task<HttpResponseMessage> request, string code, string field)
    {
        var body = await request.ExpectAsync(HttpStatusCode.BadRequest);
        Assert.Equal(code, body["error"]!.Str("code"));
        Assert.Equal(field, body["error"]!.Str("field"));
    }

    /// <summary>A random account that passes the CNB check-digit rule, so tests never collide.</summary>
    internal static CzechBankAccount NewAccount()
    {
        int[] weights = [6, 3, 7, 9, 10, 5, 8, 4, 2, 1];
        while (true)
        {
            var digits = new int[10];
            digits[0] = RandomNumberGenerator.GetInt32(1, 10);
            for (var i = 1; i < 9; i++)
            {
                digits[i] = RandomNumberGenerator.GetInt32(10);
            }

            var sum = Enumerable.Range(0, 9).Sum(i => digits[i] * weights[i]);
            var check = (11 - sum % 11) % 11;
            if (check == 10)
            {
                continue;
            }

            digits[9] = check;
            return new CzechBankAccount("000000", string.Concat(digits), "0800");
        }
    }

    /// <summary>Changes the check digit (weight 1) before the bank code, which always breaks the mod-11 rule.</summary>
    private static string BreakCheckDigit(string accountNumber)
    {
        var i = accountNumber.IndexOf('/') - 1;
        var digit = accountNumber[i] - '0';
        return accountNumber[..i] + (char)('0' + (digit + 1) % 10) + accountNumber[(i + 1)..];
    }
}
