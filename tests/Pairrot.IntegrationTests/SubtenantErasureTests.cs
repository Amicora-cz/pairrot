using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pairrot.Api.Endpoints;
using Pairrot.Core.Inbound;
using Pairrot.Core.Jobs;
using Pairrot.Core.Tenants;
using Pairrot.Infrastructure.Erasure;
using Pairrot.Infrastructure.Inbound;
using Pairrot.Infrastructure.Jobs;
using Pairrot.Infrastructure.Matching;
using Pairrot.Infrastructure.Persistence;
using Pairrot.Infrastructure.Tenants;
using Pairrot.Infrastructure.Webhooks;
using Pairrot.Tests.Shared;

#pragma warning disable EF1002 // The dump scan quotes catalog identifiers; every value is a parameter.

namespace Pairrot.IntegrationTests;

/// <summary>
/// Feature 014: erasure of a deleted subtenant, the collection barrier against in-flight and late
/// work, and payer-data retention. Every personal value a test seeds contains a unique marker, and
/// a scan of every text column of the database proves where it is (not) left.
/// </summary>
[Collection(PairrotCollection.Name)]
public class SubtenantErasureTests(PairrotFixture fx)
{
    private WebhookHarness W => fx.Webhooks;
    private ParsingHarness P => fx.Parsing;

    private sealed record Root(Guid Id, string TenantKey, string ManagementKey);

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

    private Task<HttpResponseMessage> EraseAsync(string managementKey, Guid childId, string reason, string? key = null) =>
        W.Client(managementKey).SendAsync(Command(HttpMethod.Post, $"/v1/subtenants/{childId}/erase", new { reason }, key));

    /// <summary>Moves the deletion back past the default minimum age of seven days.</summary>
    private Task AgeDeletionAsync(Guid tenantId) => fx.WithDbAsync(db => db.Tenants.Where(t => t.Id == tenantId)
        .ExecuteUpdateAsync(u => u.SetProperty(t => t.DeletedAt, t => t.DeletedAt!.Value.AddDays(-8))));

    private async Task<string> SubscribeAsync(string key, string basePath, string scope, string? url = null)
    {
        var client = W.Client(key);
        url ??= $"https://hooks-{Guid.NewGuid():N}.example.com/pairrot";
        await client.SendAsync(Command(HttpMethod.Post, $"{basePath}/webhook-endpoint", new { url, scope, environment = "live" }))
            .ExpectAsync(HttpStatusCode.Created);
        var current = await client.GetAsync($"{basePath}/webhook-endpoint").ExpectAsync(HttpStatusCode.OK);
        var patch = new HttpRequestMessage(HttpMethod.Patch, $"{basePath}/webhook-endpoint") { Content = JsonContent.Create(new { state = "active" }) };
        patch.Headers.IfMatch.Add(new System.Net.Http.Headers.EntityTagHeaderValue($"\"{current["version"]}\""));
        await client.SendAsync(patch).ExpectAsync(HttpStatusCode.OK);
        return url;
    }

    /// <summary>A notice whose payer and message carry the marker.</summary>
    private static (string, string?)[] Notice(Account account, string marker, string vs, string? reference = null) =>
    [
        ("Type", "incoming"), ("Account", account.Bank.AccountNumber), ("Amount", "450,00 CZK"), ("Booked", "07.10.2026"),
        ("Time", "07.10.2026 09:15"), ("VS", vs), ("Reference", reference), ("From account", "19-2000145399/0100"),
        ("From name", $"Payer {marker}"), ("Message", $"Note {marker}"),
    ];

    /// <summary>An inline delivery whose subject, transport payload, and metadata carry the marker.</summary>
    private static InboundDelivery Delivery(Account account, string marker, string vs, string? reference = null) =>
        ParsingHarness.Delivery(account, SyntheticBankProfile.Notice(Notice(account, marker, vs, reference))) with
        {
            Subject = $"Notice {marker}",
            RawTransportPayload = $$"""{"payer":"{{marker}}"}""",
            TransportMetadata = $$"""{"note":"{{marker}}"}""",
        };

    private Task<T> WithServicesAsync<T>(Func<IServiceProvider, Task<T>> action) => P.WithServicesAsync(action);

    private Task<ServiceResult<ErasureRequestOutcome>> OperatorEraseAsync(Guid tenantId, string reason) =>
        WithServicesAsync(services => services.GetRequiredService<TenantErasureService>()
            .RequestAsOperatorAsync(tenantId, reason, Actor.Administrator("operator:test")));

    private Task<TenantErasure?> ErasureAsync(Guid tenantId) =>
        P.WithDbAsync(db => db.TenantErasures.AsNoTracking().SingleOrDefaultAsync(e => e.TenantId == tenantId));

    private sealed class ColumnRef
    {
        public string TableName { get; set; } = "";
        public string ColumnName { get; set; } = "";
    }

    /// <summary>Like a full dump: every text, varchar, jsonb, and array column of every table that contains a needle.</summary>
    private Task<List<string>> FindAsync(params string[] needles) => P.WithDbAsync(async db =>
    {
        var columns = await db.Database.SqlQuery<ColumnRef>($"""
            SELECT table_name, column_name FROM information_schema.columns
            WHERE table_schema = 'public' AND data_type IN ('text', 'character varying', 'jsonb', 'ARRAY')
            """).ToListAsync();
        var found = new List<string>();
        foreach (var column in columns)
        {
            foreach (var needle in needles)
            {
                var hits = await db.Database.SqlQueryRaw<int>(
                    $"SELECT count(*)::int AS \"Value\" FROM \"{column.TableName}\" WHERE \"{column.ColumnName}\"::text ILIKE '%' || {{0}} || '%'",
                    needle).SingleAsync();
                if (hits > 0)
                {
                    found.Add($"{column.TableName}.{column.ColumnName}");
                }
            }
        }

        return found.Distinct().Order().ToList();
    });

    [Fact] // AC 1, 2, 4, 5, 7, 8
    public async Task Erasing_a_deleted_subtenant_purges_its_personal_data_and_keeps_its_financial_history()
    {
        var marker = $"mk{Guid.NewGuid():N}"[..14];
        var root = await NewRootAsync();
        var management = W.Client(root.ManagementKey);
        var parentUrl = await SubscribeAsync(root.ManagementKey, "/v1/management", "own_and_direct_children");
        WebhookHttpResponse Failing(WebhookHttpRequest _) => new(500, null, null, $"error for {marker}", null);
        W.Receiver.Script(parentUrl, Failing, Failing);

        var child = await fx.CreateChildAsync($"studio-{marker}", $"owner-{marker}@example.com", root.ManagementKey);
        await management.PatchJsonAsync($"/v1/subtenants/{child.Id}", new { name = $"Erasable Studio {marker}" }).ExpectAsync(HttpStatusCode.OK);
        var account = await P.CreateAccountAsync(child.Id, child.Key);
        await fx.WithDbAsync(async db =>
        {
            await db.BankConnections.Where(b => b.Id == account.ConnectionId).ExecuteUpdateAsync(u => u.SetProperty(b => b.Label, $"Account of {marker}"));
            db.ForwardingSources.Add(new ForwardingSource
            {
                Id = Guid.CreateVersion7(), TenantId = child.Id, BankConnectionId = account.ConnectionId, Address = $"fwd-{marker}@example.com",
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        });
        var childUrl = await SubscribeAsync(child.Key, "/v1/tenant", "own", $"https://hooks-{marker}.example.com/pairrot");
        W.Receiver.Script(childUrl, _ => new WebhookHttpResponse(200, null, null, $"thanks {marker}", null));

        var childClient = W.Client(child.Key);
        var requestInput = new
        {
            bank_connection_id = account.ConnectionId, external_id = $"booking-{marker}@mail.example.com", amount = 45000, currency = "CZK",
            variable_symbol = "4242", description = $"Lesson for {marker}", message = $"Lekce {marker}",
        };
        var request = await childClient.PostAsJsonAsync("/v1/tenant/payment-requests", requestInput).ExpectAsync(HttpStatusCode.Created);
        var cancelled = await childClient.PostAsJsonAsync("/v1/tenant/payment-requests", new
        {
            bank_connection_id = account.ConnectionId, external_id = $"other-{marker}", amount = 30000, currency = "CZK", variable_symbol = "4343",
        }).ExpectAsync(HttpStatusCode.Created);
        var cancel = new HttpRequestMessage(HttpMethod.Post, $"/v1/tenant/payment-requests/{cancelled.Str("id")}/cancel")
        {
            Content = JsonContent.Create(new { reason = $"Cancelled because {marker}" }),
        };
        cancel.Headers.IfMatch.Add(new System.Net.Http.Headers.EntityTagHeaderValue($"\"{cancelled["version"]}\""));
        await childClient.SendAsync(cancel).ExpectAsync(HttpStatusCode.OK);

        // The payment, and a stray movement the tenant ignores with a free-text reason.
        await P.DeliverAsync(Delivery(account, marker, "4242", $"R{Random.Shared.NextInt64(1_000_000_000, 9_999_999_999)}"));
        var strayMessage = await P.DeliverAsync(Delivery(account, marker, "999999"));
        var stray = (await childClient.GetAsync("/v1/tenant/transactions").ExpectAsync(HttpStatusCode.OK)).Items()
            .Single(t => t!["variable_symbol"]?.GetValue<string>() == "999999")!;
        await childClient.SendAsync(Command(HttpMethod.Post, $"/v1/tenant/transactions/{stray.Str("id")}/ignore",
            new { expected_version = stray["version"]!.GetValue<int>(), reason = $"Ignored {marker}" }, $"ignore-{marker}")).ExpectAsync(HttpStatusCode.OK);
        await W.RunDeliveriesAsync();
        Assert.NotEmpty(W.Receiver.To(childUrl));

        var paidBefore = await childClient.GetAsync($"/v1/tenant/payment-requests/{request.Str("id")}").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("paid", paidBefore.Str("status"));
        Assert.NotEmpty(await FindAsync(marker));

        // Only a deleted child past the minimum age, only by its parent.
        await EraseAsync(root.ManagementKey, child.Id, "too soon").ExpectErrorAsync(HttpStatusCode.Conflict, "tenant_not_deleted");
        await management.DeleteAsync($"/v1/subtenants/{child.Id}").ExpectAsync(HttpStatusCode.NoContent);
        await EraseAsync(root.ManagementKey, child.Id, "too soon").ExpectErrorAsync(HttpStatusCode.Conflict, "erasure_too_early");
        await EraseAsync(fx.RootManagementKey, child.Id, "not mine").ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");
        await management.PostAsJsonAsync($"/v1/subtenants/{child.Id}/erase", new { reason = "no key" })
            .ExpectErrorAsync(HttpStatusCode.BadRequest, "idempotency_key_required");
        await AgeDeletionAsync(child.Id);

        var response = await EraseAsync(root.ManagementKey, child.Id, $"GDPR request {marker}", $"erase-{marker}");
        Assert.Equal("false", response.Headers.GetValues("Idempotent-Replayed").Single());
        var queued = await Task.FromResult(response).ExpectAsync(HttpStatusCode.Accepted);
        Assert.Equal("queued", queued["erasure"]!.Str("state"));
        var jobId = queued["erasure"]!.Str("job_id");

        // The parent's pending delivery of the child's event is abandoned at once.
        var parentDeliveries = (await management.GetAsync("/v1/management/webhook-deliveries").ExpectAsync(HttpStatusCode.OK)).Items()
            .Where(d => d!.Str("owner_tenant_id") == child.Id.ToString()).ToList();
        Assert.Equal(2, parentDeliveries.Count);
        Assert.All(parentDeliveries, d => Assert.Equal(("abandoned", "tenant_erased"), (d!.Str("state"), d!.Str("abandon_reason"))));

        // A repeat, with any key, returns the same erasure.
        var repeat = await EraseAsync(root.ManagementKey, child.Id, "again");
        Assert.Equal("true", repeat.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal(jobId, (await Task.FromResult(repeat).ExpectAsync(HttpStatusCode.Accepted))["erasure"]!.Str("job_id"));

        await P.RunJobsAsync();
        var erased = await management.GetAsync($"/v1/subtenants/{child.Id}").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("done", erased["erasure"]!.Str("state"));
        Assert.NotNull(erased["erased_at"]);
        Assert.Equal(($"erased:{child.Id}", "erased"), (erased.Str("external_id"), erased["configuration"]!.Str("name")));
        Assert.Null(erased["configuration"]!["contact_email"]);
        Assert.Equal(2, erased["erasure"]!["counts"]!["payment_requests"]!.GetValue<int>());

        // A full scan finds no seeded value; the account itself stays only as the retained account identity.
        Assert.Empty(await FindAsync(marker, childUrl));
        Assert.Equal(["bank_accounts.iban"], await FindAsync(account.Bank.Iban));
        var erasure = (await ErasureAsync(child.Id))!;
        Assert.Null(erasure.Reason);
        Assert.Contains("sent_webhook_copies_not_recalled", erasure.Limitations);
        Assert.Single(await P.WithDbAsync(db => db.AuditEvents.Where(e => e.TargetTenantId == child.Id && e.Action == AuditActions.TenantErased).ToListAsync()));

        // Financial facts, internal IDs, and sequences are unchanged; references are tombstones.
        var paid = await management.GetAsync($"/v1/subtenants/{child.Id}/payment-requests/{request.Str("id")}").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(("paid", 45000L, 1, "4242"), (paid.Str("status"), paid["paid_amount"]!.GetValue<long>(),
            paid["payment_change_sequence"]!.GetValue<int>(), paid.Str("variable_symbol")));
        Assert.Equal($"erased:{request.Str("id")}", paid.Str("external_id"));
        Assert.Null(paid["description"]);
        Assert.Null(paid["message"]);
        Assert.Matches("^CZ…[0-9]{4}…[0-9]{2}$", paid.Str("iban"));
        Assert.EndsWith("/0800", paid.Str("account_number"));
        var transactions = (await management.GetAsync($"/v1/subtenants/{child.Id}/transactions").ExpectAsync(HttpStatusCode.OK)).Items();
        Assert.Equal(2, transactions.Count);
        Assert.All(transactions, t =>
        {
            Assert.Equal(45000L, t!["amount"]!.GetValue<long>());
            Assert.Null(t["sender_name"]);
            Assert.Null(t["message"]);
            Assert.NotNull(t["erased_at"]);
        });
        Assert.Contains(transactions, t => t!["bank_transaction_id"] is not null);
        Assert.Empty(await WithServicesAsync(s => s.GetRequiredService<MatchingScheduling>().CheckConsistencyAsync()));
        var connection = await management.GetAsync($"/v1/subtenants/{child.Id}/bank-connections/{account.ConnectionId}").ExpectAsync(HttpStatusCode.OK);
        Assert.Null(connection["label"]);
        Assert.NotNull(connection["erased_at"]);

        // Erased content is reported as erased, and nothing re-creates or retrieves it.
        var message = await management.GetAsync($"/v1/subtenants/{child.Id}/inbound-messages/{strayMessage}").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("erased", message.Str("content_state"));
        Assert.Null(message["subject"]);
        Assert.Equal([account.Address], message["envelope_recipients"]!.AsArray().Select(r => r!.GetValue<string>()));
        await management.GetAsync($"/v1/subtenants/{child.Id}/inbound-messages/{strayMessage}/source")
            .ExpectErrorAsync(HttpStatusCode.Conflict, "content_erased");
        await management.PostAsync($"/v1/subtenants/{child.Id}/inbound-messages/{strayMessage}/reprocess", null)
            .ExpectErrorAsync(HttpStatusCode.Conflict, "content_erased");
        await management.PostAsJsonAsync($"/v1/subtenants/{child.Id}/payment-requests", requestInput)
            .ExpectErrorAsync(HttpStatusCode.Conflict, "tenant_erased");
        await childClient.GetAsync("/v1/tenant/payment-requests").ExpectAsync(HttpStatusCode.Unauthorized);

        // Event bodies are gone and are never rebuilt or sent again, also not to the parent.
        var events = (await management.GetAsync("/v1/management/webhook-events").ExpectAsync(HttpStatusCode.OK)).Items()
            .Where(e => e!.Str("tenant_id") == child.Id.ToString()).ToList();
        Assert.Equal(2, events.Count(e => e!.Str("type") == "payment_request.updated"));
        Assert.All(events, e =>
        {
            Assert.Null(e!["body"]);
            Assert.NotNull(e["payload_erased_at"]);
            Assert.Matches("^[0-9a-f]{64}$", e.Str("body_sha256"));
        });
        await management.SendAsync(Command(HttpMethod.Post, "/v1/management/webhook-endpoint/replay", new
        {
            event_ids = new[] { events[0]!.Str("id") }, expected_destination_revision = 1, reason = "resend",
        })).ExpectErrorAsync(HttpStatusCode.Conflict, "event_payload_erased");
        var abandoned = parentDeliveries[0]!;
        var current = await management.GetAsync($"/v1/management/webhook-deliveries/{abandoned.Str("id")}").ExpectAsync(HttpStatusCode.OK);
        await management.SendAsync(Command(HttpMethod.Post, $"/v1/management/webhook-deliveries/{abandoned.Str("id")}/retry", new
        {
            expected_version = current["delivery"]!["version"]!.GetValue<int>(), reason = "retry",
        })).ExpectErrorAsync(HttpStatusCode.Conflict, "event_payload_erased");

        // The parent's endpoint and its other children are unaffected.
        var sibling = await fx.CreateChildAsync(managementKey: root.ManagementKey);
        var siblingAccount = await P.CreateAccountAsync(sibling.Id, sibling.Key);
        await W.Client(sibling.Key).PostAsJsonAsync("/v1/tenant/payment-requests", new
        {
            bank_connection_id = siblingAccount.ConnectionId, external_id = "sibling-booking", amount = 45000, currency = "CZK", variable_symbol = "5151",
        }).ExpectAsync(HttpStatusCode.Created);
        await P.DeliverAsync(siblingAccount, ParsingHarness.Incoming(siblingAccount.Bank, vs: "5151"));
        var before = W.Receiver.To(parentUrl).Count;
        await W.RunDeliveriesAsync();
        Assert.Equal(before + 1, W.Receiver.To(parentUrl).Count);

        // A repeat after completion changes nothing.
        var again = await EraseAsync(root.ManagementKey, child.Id, "again");
        Assert.Equal("done", (await Task.FromResult(again).ExpectAsync(HttpStatusCode.Accepted))["erasure"]!.Str("state"));

        // AC 5: the account is registered by a new tenant; its symbols still conflict.
        var successor = await fx.CreateChildAsync(managementKey: root.ManagementKey);
        var reused = await P.CreateAccountAsync(successor.Id, successor.Key, bank: account.Bank);
        await W.Client(successor.Key).PostAsJsonAsync("/v1/tenant/payment-requests", new
        {
            bank_connection_id = reused.ConnectionId, external_id = "successor-booking", amount = 45000, currency = "CZK", variable_symbol = "4242",
        }).ExpectErrorAsync(HttpStatusCode.Conflict, "variable_symbol_conflict");
    }

    [Fact] // AC 3
    public async Task An_interrupted_erasure_is_resumed_by_another_worker_with_the_same_counts()
    {
        var factory = P.Factory.WithWebHostBuilder(builder => builder.UseSetting("Erasure:BatchSize", "1"));
        async Task<Guid> SeedDeletedChildAsync(string marker)
        {
            var child = await fx.CreateChildAsync($"twin-{marker}", $"twin-{marker}@example.com");
            var account = await P.CreateAccountAsync(child.Id, child.Key);
            for (var i = 0; i < 3; i++)
            {
                await P.Client(child.Key).PostAsJsonAsync("/v1/tenant/payment-requests", new
                {
                    bank_connection_id = account.ConnectionId, external_id = $"{marker}-{i}", amount = 45000, currency = "CZK",
                    variable_symbol = $"{60 + i}", description = $"Lesson {marker}",
                }).ExpectAsync(HttpStatusCode.Created);
                await P.DeliverAsync(Delivery(account, marker, $"{60 + i}"));
            }

            await fx.Client(fx.RootManagementKey).DeleteAsync($"/v1/subtenants/{child.Id}").ExpectAsync(HttpStatusCode.NoContent);
            await AgeDeletionAsync(child.Id);
            return child.Id;
        }

        var markerA = $"ia{Guid.NewGuid():N}"[..14];
        var markerB = $"ib{Guid.NewGuid():N}"[..14];
        var interrupted = await SeedDeletedChildAsync(markerA);
        var uninterrupted = await SeedDeletedChildAsync(markerB);
        await P.RunJobsAsync();

        await EraseAsync(fx.RootManagementKey, interrupted, "interrupted").ExpectAsync(HttpStatusCode.Accepted);
        using var scope = factory.Services.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<BackgroundJobQueue>();
        var lease = (await queue.ClaimNextAsync([BackgroundJobTypes.EraseTenant], "worker-a", CancellationToken.None))!;
        Assert.Equal(interrupted, lease.TargetId);

        // Worker A starts; once it committed a batch, another worker takes the job over (A's lease is lost).
        var handler = scope.ServiceProvider.GetServices<IBackgroundJobHandler>().OfType<TenantErasureJob>().Single();
        var running = Task.Run(() => handler.HandleAsync(lease, CancellationToken.None));
        while ((await ErasureAsync(interrupted))!.Counts == "{}")
        {
            await Task.Delay(1);
        }

        await fx.WithDbAsync(db => db.Database.ExecuteSqlAsync(
            $"UPDATE background_jobs SET lease_token = gen_random_uuid(), lease_expires_at = now() - interval '1 minute' WHERE id = {lease.JobId}"));
        await running;
        Assert.Equal(TenantErasureState.Running, (await ErasureAsync(interrupted))!.State);

        // Worker B resumes from where A stopped.
        using (var other = factory.Services.CreateScope())
        {
            await other.ServiceProvider.GetRequiredService<BackgroundJobRunner>().RunDueAsync();
        }

        await EraseAsync(fx.RootManagementKey, uninterrupted, "uninterrupted").ExpectAsync(HttpStatusCode.Accepted);
        await P.RunJobsAsync();

        var resumed = (await ErasureAsync(interrupted))!;
        var single = (await ErasureAsync(uninterrupted))!;
        Assert.Equal((TenantErasureState.Done, TenantErasureState.Done), (resumed.State, single.State));
        Assert.Equal(
            System.Text.Json.JsonSerializer.Deserialize<SortedDictionary<string, int>>(single.Counts),
            System.Text.Json.JsonSerializer.Deserialize<SortedDictionary<string, int>>(resumed.Counts));
        Assert.Empty(await FindAsync(markerA, markerB));
    }

    [Fact] // AC 9
    public async Task Work_in_flight_and_late_mail_never_bring_erased_content_back()
    {
        var marker = $"if{Guid.NewGuid():N}"[..14];
        var root = await NewRootAsync();
        var child = await fx.CreateChildAsync($"flight-{marker}", managementKey: root.ManagementKey);
        var account = await P.CreateAccountAsync(child.Id, child.Key);
        var url = await SubscribeAsync(child.Key, "/v1/tenant", "own", $"https://hooks-{marker}.example.com/x");
        await P.Client(child.Key).PostAsJsonAsync("/v1/tenant/payment-requests", new
        {
            bank_connection_id = account.ConnectionId, external_id = $"booking-{marker}", amount = 45000, currency = "CZK", variable_symbol = "7171",
        }).ExpectAsync(HttpStatusCode.Created);
        var deferred = await P.CreateAccountAsync(child.Id, child.Key, InboundHarness.DeferredInstanceId);

        // A parse is in flight: the parser has read the content when the tenant is erased.
        var first = Delivery(account, marker, "7171");
        var accepted = await P.AcceptAsync(first);
        using var parsing = new SemaphoreSlim(0);
        using var release = new SemaphoreSlim(0);
        P.Profile.Adjust = fields =>
        {
            // Every extraction of the message waits until the erasure was requested.
            parsing.Release();
            release.Wait();
            return fields;
        };
        Task<int> parse;
        try
        {
            parse = Task.Run(P.RunJobsAsync);
            await parsing.WaitAsync();

            // A webhook send of a later change will also be in flight; first the deferred retrieval is queued.
            var reference = $"ref-{Guid.NewGuid():N}"; // An opaque provider ID, which erasure keeps.
            fx.Inbound.Sources.Set(reference, new ContentRetrieval(ContentRetrievalOutcome.Retrieved, new InboundContentSnapshot(
                $"Body {marker}", null, null, null, null, ParsingHarness.SmtpPassing, $"raw {marker}")));
            var pending = await P.AcceptAsync(Delivery(deferred, marker, "7272") with { Content = null, ContentReference = reference });

            Assert.Null((await OperatorEraseAsync(child.Id, $"operator erasure {marker}")).Error);
            release.Release(100);
            await parse;

            Assert.Empty(await P.WithDbAsync(db => db.MessageParseRuns.Where(r => r.InboundMessageId == accepted.MessageId).ToListAsync()));
            var parseJob = await P.WithDbAsync(db => db.BackgroundJobs.SingleAsync(j => j.Type == BackgroundJobTypes.ParseInboundMessage && j.TargetId == accepted.MessageId));
            Assert.Equal(("content_erased", BackgroundJobState.Failed), (parseJob.LastErrorCode, parseJob.State));
            var fetchJob = await P.WithDbAsync(db => db.BackgroundJobs.SingleAsync(j => j.Type == BackgroundJobTypes.FetchInboundContent && j.TargetId == pending.MessageId));
            Assert.Equal("content_erased", fetchJob.LastErrorCode);
        }
        finally
        {
            P.Profile.Adjust = null;
        }

        // Mail after erasure, a provider retry, and a late duplicate keep only the tombstone.
        var late = await P.AcceptAsync(Delivery(account, marker, "7373"));
        var retry = await P.AcceptAsync(first with { SourceDeliveryId = $"d_{Guid.NewGuid():N}", RawTransportPayload = $"retry {marker}" });
        Assert.Equal((IntakeStatus.Accepted, IntakeStatus.Accepted), (late.Status, retry.Status));
        await P.RunJobsAsync();
        var erasure = (await ErasureAsync(child.Id))!;
        Assert.Equal(TenantErasureState.Done, erasure.State);
        var lateMessage = await P.WithDbAsync(db => db.InboundMessages.AsNoTracking().SingleAsync(m => m.Id == late.MessageId));
        Assert.Equal((InboundContentState.Erased, null, null), (lateMessage.ContentState, lateMessage.Subject, lateMessage.TextBody));
        Assert.Empty(await FindAsync(marker, url));
        Assert.Empty(await P.WithDbAsync(db => db.BackgroundJobs.Where(j => j.TargetId == late.MessageId).ToListAsync()));
    }

    [Fact] // AC 8, 9: a send already in flight finishes before the erasure is done, and its response text is not kept
    public async Task An_in_flight_webhook_send_is_waited_for_and_its_response_is_not_stored()
    {
        var marker = $"ws{Guid.NewGuid():N}"[..14];
        var root = await NewRootAsync();
        var child = await fx.CreateChildAsync(managementKey: root.ManagementKey);
        var account = await P.CreateAccountAsync(child.Id, child.Key);
        var url = await SubscribeAsync(child.Key, "/v1/tenant", "own");
        await P.Client(child.Key).PostAsJsonAsync("/v1/tenant/payment-requests", new
        {
            bank_connection_id = account.ConnectionId, external_id = $"booking-{marker}", amount = 45000, currency = "CZK", variable_symbol = "8181",
        }).ExpectAsync(HttpStatusCode.Created);
        await P.DeliverAsync(Delivery(account, marker, "8181"));

        using var sending = new SemaphoreSlim(0);
        using var answer = new SemaphoreSlim(0);
        W.Receiver.Script(url, _ =>
        {
            sending.Release();
            answer.Wait();
            return new WebhookHttpResponse(400, null, $"rejected {marker}", $"body {marker}", null);
        });
        var send = Task.Run(W.RunDeliveriesAsync);
        await sending.WaitAsync();

        Assert.Null((await OperatorEraseAsync(child.Id, "operator erasure")).Error);
        await P.RunJobsAsync();
        Assert.Equal(TenantErasureState.Running, (await ErasureAsync(child.Id))!.State);

        answer.Release();
        await send;
        W.Clock.Advance(TimeSpan.FromMinutes(10));
        await P.RunJobsAsync();
        Assert.Equal(TenantErasureState.Done, (await ErasureAsync(child.Id))!.State);
        var attempts = await P.WithDbAsync(db => db.WebhookDeliveryAttempts.AsNoTracking()
            .Where(a => db.WebhookDeliveries.Any(d => d.Id == a.DeliveryId && d.OwnerTenantId == child.Id)).ToListAsync());
        var attempt = Assert.Single(attempts);
        Assert.Equal((400, null, null), (attempt.HttpStatus, attempt.ResponseExcerpt, attempt.Url));
        Assert.Empty(await FindAsync(marker, url));
    }

    [Fact]
    public async Task The_operator_erases_a_root_after_its_children_and_deletes_it_first()
    {
        var root = await NewRootAsync();
        var child = await fx.CreateChildAsync(managementKey: root.ManagementKey);
        Assert.Equal("tenant_has_children", (await OperatorEraseAsync(root.Id, "erase root")).Error?.Code);

        await fx.Client(root.ManagementKey).DeleteAsync($"/v1/subtenants/{child.Id}").ExpectAsync(HttpStatusCode.NoContent);
        var outcome = (await OperatorEraseAsync(root.Id, "erase root")).Value!;
        Assert.False(outcome.Replayed);
        await P.RunJobsAsync();
        var tenant = await P.WithDbAsync(db => db.Tenants.AsNoTracking().SingleAsync(t => t.Id == root.Id));
        Assert.Equal((TenantStatus.Deleted, "erased", $"erased:{root.Id}"), (tenant.Status, tenant.Name, tenant.ExternalId));
        Assert.NotNull(tenant.ErasedAt);
        await fx.Client(root.TenantKey).GetAsync("/v1/tenant").ExpectAsync(HttpStatusCode.Unauthorized);
        Assert.True((await OperatorEraseAsync(root.Id, "again")).Value!.Replayed);
    }

    [Fact] // AC 6, 7
    public async Task Retention_purges_payer_data_at_the_configured_age_and_nothing_else()
    {
        var marker = $"rt{Guid.NewGuid():N}"[..14];
        var youngMarker = $"ry{Guid.NewGuid():N}"[..14];
        var reference = $"booking-{Guid.NewGuid():N}";
        var child = await fx.CreateChildAsync();
        var live = await P.CreateAccountAsync(child.Id, child.Key);
        var test = await P.CreateAccountAsync(child.Id, child.Key, InboundHarness.InlineInstanceId);
        var request = await P.Client(child.Key).PostAsJsonAsync("/v1/tenant/payment-requests", new
        {
            bank_connection_id = live.ConnectionId, external_id = reference, amount = 45000, currency = "CZK", variable_symbol = "9191",
        }).ExpectAsync(HttpStatusCode.Created);
        var old = await P.DeliverAsync(Delivery(live, marker, "9191"));
        var oldTest = await P.DeliverAsync(Delivery(test, marker, "9292"));
        var young = await P.DeliverAsync(Delivery(live, youngMarker, "9393"));

        var aged = W.Clock.GetUtcNow().AddDays(-731);
        await fx.WithDbAsync(async db =>
        {
            await db.InboundMessages.Where(m => m.Id == old || m.Id == oldTest).ExecuteUpdateAsync(u => u.SetProperty(m => m.ReceivedAt, aged));
            await db.IncomingTransactions.Where(t => db.TransactionSources.Any(s => s.TransactionId == t.Id && (s.InboundMessageId == old || s.InboundMessageId == oldTest)))
                .ExecuteUpdateAsync(u => u.SetProperty(t => t.ReceivedAt, aged));
        });

        var report = await WithServicesAsync(s => s.GetRequiredService<PayerDataRetention>().RunAsync());
        Assert.True(report.MessagesByInstance[ParsingHarness.SmtpInstanceId] >= 1);
        Assert.True(report.MessagesByInstance[InboundHarness.InlineInstanceId] >= 1);
        Assert.True(report.Transactions >= 2);

        // The old messages and their payers are gone; the young one is untouched; money facts stay.
        Assert.Empty(await FindAsync(marker));
        Assert.Contains("inbound_messages.subject", await FindAsync(youngMarker));
        var client = P.Client(child.Key);
        var expired = await client.GetAsync($"/v1/tenant/inbound-messages/{old}").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("expired", expired.Str("content_state"));
        await client.GetAsync($"/v1/tenant/inbound-messages/{old}/source").ExpectErrorAsync(HttpStatusCode.Conflict, "content_expired");
        await client.PostAsync($"/v1/tenant/inbound-messages/{old}/reprocess", null).ExpectErrorAsync(HttpStatusCode.Conflict, "content_expired");
        await client.PostAsync($"/v1/tenant/inbound-messages/{old}/retry-fetch", null).ExpectErrorAsync(HttpStatusCode.Conflict, "content_expired");
        var kept = await client.GetAsync($"/v1/tenant/inbound-messages/{young}").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(("available", $"Notice {youngMarker}"), (kept.Str("content_state"), kept.Str("subject")));

        var transactions = (await client.GetAsync("/v1/tenant/transactions").ExpectAsync(HttpStatusCode.OK)).Items();
        var purged = transactions.Where(t => t!["payer_data_purged_at"] is not null).ToList();
        Assert.Equal(2, purged.Count);
        Assert.All(purged, t => Assert.Equal((45000L, (string?)null), (t!["amount"]!.GetValue<long>(), t["sender_name"]?.GetValue<string>())));
        Assert.Equal($"Payer {youngMarker}", transactions.Single(t => t!["payer_data_purged_at"] is null)!.Str("sender_name"));
        var paid = await client.GetAsync($"/v1/tenant/payment-requests/{request.Str("id")}").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(("paid", reference), (paid.Str("status"), paid.Str("external_id")));

        // A delayed delivery older than the limit is acknowledged but stores nothing about its payer.
        var delayed = await P.AcceptAsync(Delivery(live, $"delayed{marker}", "9494") with { ReceivedAt = aged });
        var delayedMessage = await P.WithDbAsync(db => db.InboundMessages.AsNoTracking().SingleAsync(m => m.Id == delayed.MessageId));
        Assert.Equal((InboundContentState.Expired, null), (delayedMessage.ContentState, delayedMessage.Subject));
        Assert.Empty(await FindAsync($"delayed{marker}"));
    }
}
