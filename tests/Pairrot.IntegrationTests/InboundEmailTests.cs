using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pairrot.Core.Inbound;
using Pairrot.Core.Jobs;
using Pairrot.Core.Tenants;
using Pairrot.Infrastructure.Inbound;
using Pairrot.Infrastructure.Jobs;
using Pairrot.Infrastructure.Persistence;

namespace Pairrot.IntegrationTests;

/// <summary>
/// Feature 006 acceptance criteria 1-7, with signed fixture events and a fake Receiving API.
/// These prove transport handling only; live reception (criterion 8) is recorded separately.
/// </summary>
[Collection(PairrotCollection.Name)]
public class InboundEmailTests(PairrotFixture fx)
{
    private const string Messages = "/v1/tenant/inbound-messages";
    private const string Connections = "/v1/tenant/bank-connections";

    private InboundHarness Inbound => fx.Inbound;

    [Fact] // AC 1
    public async Task Existing_stub_addresses_survive_the_migration_unchanged()
    {
        var database = $"migration_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(fx.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {database}", admin);
            await create.ExecuteNonQueryAsync();
        }

        var connectionString = new NpgsqlConnectionStringBuilder(fx.ConnectionString) { Database = database }.ConnectionString;
        var options = new DbContextOptionsBuilder<PairrotDbContext>().UseNpgsql(connectionString).UseSnakeCaseNamingConvention().Options;
        await using var db = new PairrotDbContext(options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261007062942_PaymentRequests");

        var tenantId = Guid.CreateVersion7();
        var connectionId = Guid.CreateVersion7();
        const string address = "p_abcdefghijklmnopqrstuvwx@in.pairrot.test";
        var createdAt = DateTimeOffset.Parse("2026-10-06T19:00:00Z");
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO tenants (id, external_id, name, status, can_create_subtenants, can_create_campaigns, created_at, updated_at)
            VALUES ({tenantId}, 'migrated', 'Migrated', 'active', false, false, {createdAt}, {createdAt});
            INSERT INTO bank_connections (id, tenant_id, account_number, iban, currency, status, email_provider, inbound_address,
                address_status, address_created_at, created_at, updated_at)
            VALUES ({connectionId}, {tenantId}, '19-2000145399/0800', 'CZ6508000000192000145399', 'CZK', 'active', 'stub', {address},
                'stub_created', {createdAt}, {createdAt}, {createdAt});
            """);

        await migrator.MigrateAsync();

        var migrated = Assert.Single(await db.InboundAddresses.ToListAsync());
        Assert.Equal(address, migrated.Address);
        Assert.Equal(tenantId, migrated.TenantId);
        Assert.Equal(connectionId, migrated.BankConnectionId);
        Assert.Equal(("stub", "stub", InboundEnvironment.Test), (migrated.ProviderInstanceId, migrated.ProviderKind, migrated.Environment));
        Assert.Equal((InboundAddressState.Active, InboundProvisioningState.Ready), (migrated.State, migrated.ProvisioningState));
        Assert.Equal(createdAt, migrated.CreatedAt);

        // A test address can never become a live one, even by a direct write.
        await Assert.ThrowsAnyAsync<PostgresException>(() => db.Database.ExecuteSqlAsync(
            $"UPDATE inbound_addresses SET environment = 'live' WHERE id = {migrated.Id}"));
    }

    [Fact] // AC 1
    public async Task Live_allocation_replaces_a_stub_once_and_a_stub_never_becomes_live()
    {
        var child = await fx.CreateChildAsync();
        var stub = fx.Client(child.Key);
        var live = Inbound.Client(child.Key);
        var connection = await stub.PostAsJsonAsync(Connections, new { account_number = BankConnectionTests.NewAccount().AccountNumber })
            .ExpectAsync(HttpStatusCode.Created);
        var path = $"{Connections}/{connection.Str("id")}";
        var stubAddress = (await stub.PostAsync($"{path}/inbound-address", null).ExpectAsync(HttpStatusCode.OK)).Str("inbound_address");
        Assert.EndsWith("@in.pairrot.test", stubAddress);

        // Switching the server to Resend rewrites nothing by itself.
        var before = await live.GetAsync(path).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("stub_created", before.Str("address_status"));
        Assert.Equal("stub", before.Str("email_provider"));
        Assert.Equal(stubAddress, before.Str("inbound_address"));

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => live.PostAsync($"{path}/inbound-address", null)));
        var bodies = new List<JsonNode>();
        foreach (var response in responses)
        {
            bodies.Add(await Task.FromResult(response).ExpectAsync(HttpStatusCode.OK));
        }

        var liveAddress = Assert.Single(bodies.Select(b => b.Str("inbound_address")).Distinct());
        Assert.Matches(@"^p_[a-z2-7]{24}@in\.pairrot-fixture\.net$", liveAddress);
        Assert.All(bodies, b => Assert.Equal("live_created", b.Str("address_status")));
        Assert.All(bodies, b => Assert.Equal("resend", b.Str("email_provider")));
        Assert.Equal(1, bodies.Count(b => b["address_replaced"]!.GetValue<bool>()));

        var again = await live.PostAsync($"{path}/inbound-address", null).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(liveAddress, again.Str("inbound_address"));
        Assert.False(again["address_replaced"]!.GetValue<bool>());

        // The stub host never downgrades a live address.
        var fromStubHost = await stub.PostAsync($"{path}/inbound-address", null).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(liveAddress, fromStubHost.Str("inbound_address"));
        Assert.Equal("resend", fromStubHost.Str("email_provider"));

        await fx.WithDbAsync(async db =>
        {
            var addresses = await db.InboundAddresses.Where(a => a.TenantId == child.Id).OrderBy(a => a.CreatedAt).ToListAsync();
            Assert.Equal(2, addresses.Count);
            Assert.Equal((stubAddress, "stub", InboundEnvironment.Test, InboundAddressState.Retired),
                (addresses[0].Address, addresses[0].ProviderInstanceId, addresses[0].Environment, addresses[0].State));
            Assert.NotNull(addresses[0].RetiredAt);
            Assert.Equal((liveAddress, InboundHarness.InstanceId, InboundEnvironment.Live, InboundAddressState.Active),
                (addresses[1].Address, addresses[1].ProviderInstanceId, addresses[1].Environment, addresses[1].State));
            Assert.Equal(1, await db.AuditEvents.CountAsync(e =>
                e.TargetTenantId == child.Id && e.Action == AuditActions.BankConnectionAddressReplaced));
        });

        // Mail claiming the retired stub address through Resend is quarantined: that address is bound to the stub instance.
        var claimId = InboundHarness.NewEmailId();
        await Inbound.PostEventAsync(InboundHarness.ReceivedEvent(claimId, stubAddress)).ExpectAsync(HttpStatusCode.OK);
        Assert.Empty((await live.GetAsync(Messages).ExpectAsync(HttpStatusCode.OK)).Items());
        await fx.WithDbAsync(async db => Assert.Equal(InboundRoutingState.InstanceMismatch,
            (await db.InboundMessages.SingleAsync(m => m.SourceMessageId == claimId)).RoutingState));

        // A new connection in live mode gets a live address directly.
        var second = await live.PostAsJsonAsync(Connections, new { account_number = BankConnectionTests.NewAccount().AccountNumber })
            .ExpectAsync(HttpStatusCode.Created);
        var direct = await live.PostAsync($"{Connections}/{second.Str("id")}/inbound-address", null).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("live_created", direct.Str("address_status"));
        Assert.False(direct["address_replaced"]!.GetValue<bool>());
    }

    [Fact] // AC 2
    public async Task Webhook_rejects_bad_signatures_tampering_expired_timestamps_and_oversized_bodies()
    {
        var box = await Inbound.CreateMailboxAsync();
        var body = InboundHarness.ReceivedEvent(InboundHarness.NewEmailId(), box.Address);

        await Inbound.PostEventAsync(body, secret: "whsec_" + Convert.ToBase64String(new byte[32]))
            .ExpectErrorAsync(HttpStatusCode.BadRequest, "invalid_signature");
        await Inbound.PostEventAsync(body.Replace("Příchozí platba", "Odchozí platba"), signedBody: body)
            .ExpectErrorAsync(HttpStatusCode.BadRequest, "invalid_signature");
        await Inbound.PostEventAsync(body, signedAt: DateTimeOffset.UtcNow.AddMinutes(-10))
            .ExpectErrorAsync(HttpStatusCode.BadRequest, "invalid_signature");
        await Inbound.PostEventAsync(body, signedAt: DateTimeOffset.UtcNow.AddMinutes(10))
            .ExpectErrorAsync(HttpStatusCode.BadRequest, "invalid_signature");
        await Inbound.Factory.CreateClient().PostAsync($"/v1/webhooks/resend/{InboundHarness.InstanceId}", new StringContent(body, Encoding.UTF8, "application/json"))
            .ExpectErrorAsync(HttpStatusCode.BadRequest, "invalid_signature");

        // Each instance verifies with its own secret.
        await Inbound.PostEventAsync(body, instance: InboundHarness.SecondInstanceId).ExpectErrorAsync(HttpStatusCode.BadRequest, "invalid_signature");

        // Correctly signed but unusable events.
        await Inbound.PostEventAsync("not json").ExpectErrorAsync(HttpStatusCode.BadRequest, "invalid_event");
        await Inbound.PostEventAsync("""{"type":"email.received","data":{"to":["x@in.pairrot-fixture.net"]}}""")
            .ExpectErrorAsync(HttpStatusCode.BadRequest, "invalid_event");
        await Inbound.PostEventAsync("""{"type":"email.received","data":{"email_id":"e1","to":"x@y.cz","subject":5}}""")
            .ExpectErrorAsync(HttpStatusCode.BadRequest, "invalid_event");

        var oversized = InboundHarness.ReceivedEvent(InboundHarness.NewEmailId(), box.Address).Replace("Příchozí platba", new string('x', 1024 * 1024));
        await Inbound.PostEventAsync(oversized).ExpectErrorAsync(HttpStatusCode.RequestEntityTooLarge, "payload_too_large");

        // A verified event of another type is acknowledged by the adapter; nothing reaches intake.
        var ignoredId = $"msg_{Guid.NewGuid():N}";
        var ignored = await Inbound.PostEventAsync("""{"type":"email.sent","data":{"email_id":"x"}}""", ignoredId).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("ignored", ignored.Str("status"));

        await fx.WithDbAsync(async db =>
        {
            Assert.False(await db.InboundMessages.AnyAsync(m => m.TenantId == box.TenantId));
            Assert.False(await db.InboundDeliveryReceipts.AnyAsync(r => r.SourceDeliveryId == ignoredId));
        });

        // Unknown instances, the bare path with several Resend instances, and the stub-only host have no webhook; there is no unsigned shortcut.
        await Inbound.PostEventAsync(body, instance: InboundHarness.DeferredInstanceId).ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");
        await Inbound.Factory.CreateClient().PostAsync("/v1/webhooks/resend", new StringContent(body, Encoding.UTF8, "application/json"))
            .ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");
        await fx.Client().PostAsync("/v1/webhooks/resend", new StringContent(body, Encoding.UTF8, "application/json"))
            .ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");
    }

    [Fact] // AC 2, AC 3
    public async Task Duplicate_and_concurrent_deliveries_create_one_receipt_message_and_job()
    {
        var box = await Inbound.CreateMailboxAsync();
        var emailId = InboundHarness.NewEmailId();
        var body = InboundHarness.ReceivedEvent(emailId, box.Address);
        var eventId = $"msg_{Guid.NewGuid():N}";

        var deliveries = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Inbound.PostEventAsync(body, eventId)));
        Assert.All(deliveries, d => Assert.Equal(HttpStatusCode.OK, d.StatusCode));

        // Another event for the same email, e.g. a provider replay with a new event ID.
        await Inbound.PostEventAsync(body.Replace("2026-10-07T07:00:00.500Z", "2026-10-07T07:05:00.000Z")).ExpectAsync(HttpStatusCode.OK);

        // The same event ID with different content is a conflict and changes nothing.
        await Inbound.PostEventAsync(body.Replace("Příchozí platba", "Jiný předmět"), eventId)
            .ExpectErrorAsync(HttpStatusCode.Conflict, "event_conflict");
        await Inbound.PostEventAsync(body, eventId).ExpectAsync(HttpStatusCode.OK);

        await fx.WithDbAsync(async db =>
        {
            var message = await db.InboundMessages.SingleAsync(m => m.SourceMessageId == emailId);
            Assert.Equal(2, await db.InboundDeliveryReceipts.CountAsync(r => r.InboundMessageId == message.Id));
            var receipt = await db.InboundDeliveryReceipts.SingleAsync(r => r.SourceDeliveryId == eventId);
            Assert.Equal(InboundReceiptOutcome.MessageCreated, receipt.Outcome);
            Assert.Equal(("resend_webhook", "svix_signature", body), (receipt.TransportKind, receipt.DeliveryEvidence, receipt.RawPayload));
            Assert.Equal((InboundContentMode.Deferred, emailId, "resend:to,cc,bcc"),
                (message.ContentMode, message.ContentReference, message.RecipientEvidence));
            Assert.Equal(1, await db.BackgroundJobs.CountAsync(j => j.TargetId == message.Id));
            Assert.Equal((InboundSourceKind.Fixture, InboundEnvironment.Live), (message.SourceKind, message.Environment));
            Assert.Equal("Příchozí platba", message.Subject);
            Assert.Equal(DateTimeOffset.Parse("2026-10-07T07:00:00Z"), message.ReceivedAt);
        });

        Inbound.Api.Respond(emailId, FakeResendApi.Ok(InboundHarness.Content(emailId, [box.Address])));
        await Inbound.RunJobsAsync();
        Assert.Equal(1, Inbound.Api.CallsFor(emailId));
    }

    [Fact] // AC 3
    public async Task Database_failure_returns_a_retryable_error_instead_of_acknowledging()
    {
        await using var broken = Inbound.CreateFactory("Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=2");
        var body = InboundHarness.ReceivedEvent(InboundHarness.NewEmailId(), "someone@in.pairrot-fixture.net");
        await Inbound.PostEventAsync(body, factory: broken).ExpectErrorAsync(HttpStatusCode.ServiceUnavailable, "temporarily_unavailable");
    }

    [Fact] // AC 4
    public async Task Recipients_route_only_through_the_live_address_map()
    {
        var first = await Inbound.CreateMailboxAsync();
        var second = await Inbound.CreateMailboxAsync();

        // Display names and case do not matter.
        var routedId = await DeliverAsync($"Platby <{first.Address.ToUpperInvariant()}>");
        var unknownId = await DeliverAsync($"nobody_{Guid.NewGuid():N}@{InboundHarness.ReceivingDomain}");
        var ambiguousId = await DeliverAsync(first.Address, second.Address);

        var firstList = await Inbound.Client(first.Key).GetAsync(Messages).ExpectAsync(HttpStatusCode.OK);
        var routed = Assert.Single(firstList.Items())!;
        Assert.Equal(routedId, routed.Str("id"));
        Assert.Equal("routed", routed.Str("routing_state"));
        Assert.Equal(first.ConnectionId.ToString(), routed.Str("bank_connection_id"));
        Assert.Null(routed["hold_reason"]);
        Assert.Empty((await Inbound.Client(second.Key).GetAsync(Messages).ExpectAsync(HttpStatusCode.OK)).Items());

        await fx.WithDbAsync(async db =>
        {
            var unknown = await db.InboundMessages.SingleAsync(m => m.Id == Guid.Parse(unknownId));
            Assert.Equal((InboundRoutingState.UnknownRecipient, (Guid?)null), (unknown.RoutingState, unknown.TenantId));
            var ambiguous = await db.InboundMessages.SingleAsync(m => m.Id == Guid.Parse(ambiguousId));
            Assert.Equal((InboundRoutingState.AmbiguousRecipient, (Guid?)null), (ambiguous.RoutingState, ambiguous.TenantId));

            // Quarantined messages wait for an operator; nothing is retrieved yet.
            Assert.False(await db.BackgroundJobs.AnyAsync(j => j.TargetId == unknown.Id || j.TargetId == ambiguous.Id));
        });

        // Neither tenant can open a quarantined message or the other's message by ID.
        foreach (var id in new[] { unknownId, ambiguousId })
        {
            await Inbound.Client(first.Key).GetAsync($"{Messages}/{id}").ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");
        }

        await Inbound.Client(second.Key).GetAsync($"{Messages}/{routedId}").ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");
        await Inbound.Client(second.Key).GetAsync($"{Messages}/{routedId}/source").ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");
        await Inbound.Client(second.Key).PostAsync($"{Messages}/{routedId}/retry-fetch", null).ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");

        // Two aliases of one connection yield one owner; a retired address alone still routes, but is held.
        var alias = $"p_alias{Guid.NewGuid():N}"[..26] + "@" + InboundHarness.ReceivingDomain;
        await fx.WithDbAsync(async db =>
        {
            db.InboundAddresses.Add(new InboundAddress
            {
                Id = Guid.CreateVersion7(), TenantId = first.TenantId, BankConnectionId = first.ConnectionId, Address = alias,
                ProviderInstanceId = InboundHarness.InstanceId, ProviderKind = "resend", Environment = InboundEnvironment.Live,
                ProvisioningState = InboundProvisioningState.Ready, State = InboundAddressState.Retired,
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-1), ReadyAt = DateTimeOffset.UtcNow.AddDays(-1), RetiredAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        });
        var bothId = await DeliverAsync(first.Address, alias);
        var retiredId = await DeliverAsync(alias);
        Assert.Null((await GetMessageAsync(first, bothId))["hold_reason"]);
        Assert.Equal("address_retired", (await GetMessageAsync(first, retiredId)).Str("hold_reason"));

        // An archived connection and an inactive owner keep mail for history, held from processing.
        var archived = await Inbound.CreateMailboxAsync();
        await Inbound.Client(archived.Key).DeleteAsync($"{Connections}/{archived.ConnectionId}").ExpectAsync(HttpStatusCode.NoContent);
        var archivedId = await DeliverAsync(archived.Address);
        Assert.Equal("bank_connection_archived", (await GetMessageAsync(archived, archivedId)).Str("hold_reason"));

        var suspended = await Inbound.CreateMailboxAsync();
        await fx.SetStatusAsync(suspended.TenantId, TenantStatus.Suspended);
        var suspendedId = await DeliverAsync(suspended.Address);
        var parent = Inbound.Client(fx.RootManagementKey);
        var held = await parent.GetAsync($"/v1/subtenants/{suspended.TenantId}/inbound-messages/{suspendedId}").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("tenant_not_active", held.Str("hold_reason"));

        // Held messages still get their content retrieved.
        await fx.WithDbAsync(async db =>
        {
            var emailId = await db.InboundMessages.Where(m => m.Id == Guid.Parse(suspendedId)).Select(m => m.SourceMessageId).SingleAsync();
            Inbound.Api.Respond(emailId, FakeResendApi.Ok(InboundHarness.Content(emailId, [suspended.Address])));
        });
        await Inbound.RunJobsAsync();
        var fetched = await parent.GetAsync($"/v1/subtenants/{suspended.TenantId}/inbound-messages/{suspendedId}").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("available", fetched.Str("content_state"));
        await fx.SetStatusAsync(suspended.TenantId, TenantStatus.Active);
    }

    [Fact] // AC 4: operator tooling
    public async Task Operators_retry_routing_only_against_the_address_map()
    {
        var box = await Inbound.CreateMailboxAsync();
        var target = $"p_late{Guid.NewGuid():N}"[..26] + "@" + InboundHarness.ReceivingDomain;
        var messageId = Guid.Parse(await DeliverAsync(target));

        var admin = (Func<InboundAdministration, Task<object?>> action) =>
            Inbound.WithServiceAsync(services => action(services.GetRequiredService<InboundAdministration>()));

        // Still unknown: the message stays quarantined.
        await admin(async a =>
        {
            var listed = await a.ListAsync(InboundAttention.Quarantined, 1000);
            Assert.Contains(listed, m => m.Id == messageId);
            var result = await a.RetryRoutingAsync(messageId, "test-operator");
            Assert.Equal(InboundRoutingState.UnknownRecipient, result.Value!.RoutingState);
            return null;
        });

        // After the address map is fixed, routing succeeds and retrieval is queued.
        await fx.WithDbAsync(async db =>
        {
            db.InboundAddresses.Add(new InboundAddress
            {
                Id = Guid.CreateVersion7(), TenantId = box.TenantId, BankConnectionId = box.ConnectionId, Address = target,
                ProviderInstanceId = InboundHarness.InstanceId, ProviderKind = "resend", Environment = InboundEnvironment.Live,
                ProvisioningState = InboundProvisioningState.Ready, State = InboundAddressState.Retired,
                CreatedAt = DateTimeOffset.UtcNow, ReadyAt = DateTimeOffset.UtcNow, RetiredAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        });

        await admin(async a =>
        {
            var result = await a.RetryRoutingAsync(messageId, "test-operator");
            Assert.Equal(InboundRoutingState.Routed, result.Value!.RoutingState);
            Assert.Equal(box.TenantId, result.Value.TenantId);
            Assert.Equal("already_routed", (await a.RetryRoutingAsync(messageId, "test-operator")).Error!.Code);
            var view = await a.GetAsync(messageId);
            var job = Assert.Single(view!.Jobs);
            Assert.Equal("operator:test-operator", job.RequestedBy);
            return null;
        });

        Assert.Equal("address_retired", (await GetMessageAsync(box, messageId.ToString())).Str("hold_reason"));
        await fx.WithDbAsync(async db => Assert.Equal(1, await db.AuditEvents.CountAsync(e =>
            e.TargetTenantId == box.TenantId && e.Action == AuditActions.InboundMessageRouted && e.ActorDescription == "test-operator")));
    }

    [Fact] // AC 5
    public async Task Retrieval_retries_survive_a_restart_and_terminal_failures_can_be_retried()
    {
        await Inbound.FlushJobsAsync(TimeSpan.FromHours(7));
        var box = await Inbound.CreateMailboxAsync();
        var client = Inbound.Client(box.Key);

        // A temporary provider failure with Retry-After waits at least that long.
        var transientEmail = InboundHarness.NewEmailId();
        Inbound.Api.Respond(transientEmail,
            FakeResendApi.Status(HttpStatusCode.ServiceUnavailable, TimeSpan.FromMinutes(10)),
            FakeResendApi.Ok(InboundHarness.Content(transientEmail, [box.Address])));
        var transientId = await DeliverEmailAsync(transientEmail, box.Address);
        await Inbound.RunJobsAsync();

        var waiting = await client.GetAsync($"{Messages}/{transientId}").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("pending", waiting.Str("content_state"));
        Assert.Equal("provider_unavailable", waiting["content_error"]!.Str("code"));
        Assert.DoesNotContain("secret detail", waiting.ToJsonString());
        var retrieval = Assert.Single(waiting["retrievals"]!.AsArray())!;
        Assert.Equal(("retry_wait", 1), (retrieval.Str("state"), retrieval["attempt_count"]!.GetValue<int>()));
        var next = DateTimeOffset.Parse(retrieval.Str("next_attempt_at"));
        Assert.InRange(next - Inbound.Clock.GetUtcNow(), TimeSpan.FromMinutes(9), TimeSpan.FromMinutes(11));

        await Inbound.RunJobsAsync();
        Assert.Equal(1, Inbound.Api.CallsFor(transientEmail)); // not due yet

        // A new process resumes the persisted job.
        Inbound.Clock.Advance(TimeSpan.FromMinutes(11));
        await using (var restarted = Inbound.CreateFactory())
        {
            await Inbound.RunJobsAsync(restarted);
        }

        var recovered = await client.GetAsync($"{Messages}/{transientId}").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("available", recovered.Str("content_state"));
        Assert.Null(recovered["content_error"]);

        // Rejected credentials fail at once and stay visible until someone retries.
        var deniedEmail = InboundHarness.NewEmailId();
        Inbound.Api.Respond(deniedEmail, FakeResendApi.Status(HttpStatusCode.Unauthorized));
        var deniedId = await DeliverEmailAsync(deniedEmail, box.Address);
        await Inbound.RunJobsAsync();
        var failed = await client.GetAsync($"{Messages}/{deniedId}").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("failed", failed.Str("content_state"));
        Assert.Equal("provider_unauthorized", failed["content_error"]!.Str("code"));
        Assert.Equal("failed", failed["retrievals"]![0]!.Str("state"));
        await client.GetAsync($"{Messages}/{deniedId}/source").ExpectErrorAsync(HttpStatusCode.Conflict, "content_not_available");

        var bank = await client.GetAsync($"{Connections}/{box.ConnectionId}").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("provider_unauthorized", bank["reception"]!["last_content_error"]!.Str("code"));
        Assert.NotNull(bank["reception"]!["last_message_received_at"]);
        Assert.NotNull(bank["reception"]!["last_content_stored_at"]);

        Inbound.Api.Respond(deniedEmail, FakeResendApi.Ok(InboundHarness.Content(deniedEmail, [box.Address])));
        var queued = await client.PostAsync($"{Messages}/{deniedId}/retry-fetch", null).ExpectAsync(HttpStatusCode.Accepted);
        Assert.True(queued["created"]!.GetValue<bool>());
        var repeated = await client.PostAsync($"{Messages}/{deniedId}/retry-fetch", null).ExpectAsync(HttpStatusCode.Accepted);
        Assert.False(repeated["created"]!.GetValue<bool>());
        Assert.Equal(queued.Str("job_id"), repeated.Str("job_id"));

        await Inbound.RunJobsAsync();
        var retried = await client.GetAsync($"{Messages}/{deniedId}").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("available", retried.Str("content_state"));
        Assert.Equal([1, 2], retried["retrievals"]!.AsArray().Select(r => r!["generation"]!.GetValue<int>()));
        await client.PostAsync($"{Messages}/{deniedId}/retry-fetch", null).ExpectErrorAsync(HttpStatusCode.Conflict, "content_already_available");
        await fx.WithDbAsync(async db => Assert.Equal(1, await db.AuditEvents.CountAsync(e =>
            e.TargetTenantId == box.TenantId && e.Action == AuditActions.InboundMessageFetchRetried)));

        // A message the provider never returns is unavailable after the configured six attempts.
        var missingEmail = InboundHarness.NewEmailId();
        var missingId = await DeliverEmailAsync(missingEmail, box.Address);
        for (var attempt = 0; attempt < 6; attempt++)
        {
            await Inbound.RunJobsAsync();
            Inbound.Clock.Advance(TimeSpan.FromHours(7));
        }

        var unavailable = await client.GetAsync($"{Messages}/{missingId}").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("failed", unavailable.Str("content_state"));
        Assert.Equal("content_unavailable", unavailable["content_error"]!.Str("code"));
        Assert.Equal(6, unavailable["retrievals"]![0]!["attempt_count"]!.GetValue<int>());
        Assert.Equal(6, Inbound.Api.CallsFor(missingEmail));
    }

    [Fact] // AC 5
    public async Task The_hosted_worker_retrieves_content_on_its_own()
    {
        var box = await Inbound.CreateMailboxAsync();
        var emailId = InboundHarness.NewEmailId();
        Inbound.Api.Respond(emailId, FakeResendApi.Ok(InboundHarness.Content(emailId, [box.Address])));
        var messageId = await DeliverEmailAsync(emailId, box.Address);

        await using var withWorker = Inbound.CreateFactory(worker: true);
        withWorker.CreateClient(); // starts the host and its worker
        var deadline = DateTime.UtcNow.AddSeconds(20);
        string state;
        do
        {
            await Task.Delay(100);
            state = (await GetMessageAsync(box, messageId)).Str("content_state");
        }
        while (state != "available" && DateTime.UtcNow < deadline);

        Assert.Equal("available", state);
    }

    [Fact] // AC 5
    public async Task A_worker_whose_lease_expired_cannot_commit_a_second_result()
    {
        await Inbound.FlushJobsAsync(TimeSpan.FromHours(7));
        var box = await Inbound.CreateMailboxAsync();
        var emailId = InboundHarness.NewEmailId();
        Inbound.Api.Respond(emailId,
            FakeResendApi.Ok(InboundHarness.Content(emailId, [box.Address], text: "first worker")),
            FakeResendApi.Ok(InboundHarness.Content(emailId, [box.Address], text: "second worker")));
        var messageId = await DeliverEmailAsync(emailId, box.Address);

        using var staleScope = Inbound.Factory.Services.CreateScope();
        var staleLease = await staleScope.ServiceProvider.GetRequiredService<BackgroundJobRunner>().ClaimAsync();
        Assert.Equal(Guid.Parse(messageId), staleLease!.TargetId);

        // The first worker stalls past its lease; another worker resumes the job.
        Inbound.Clock.Advance(TimeSpan.FromMinutes(6));
        using var freshScope = Inbound.Factory.Services.CreateScope();
        var freshLease = await freshScope.ServiceProvider.GetRequiredService<BackgroundJobRunner>().ClaimAsync();
        Assert.Equal(staleLease.JobId, freshLease!.JobId);
        Assert.Equal(2, freshLease.AttemptCount);

        var handler = (IServiceScope scope) => scope.ServiceProvider.GetServices<IBackgroundJobHandler>().Single(h => h.Type == BackgroundJobTypes.FetchInboundContent);
        await handler(staleScope).HandleAsync(staleLease, CancellationToken.None);
        var afterStale = await GetMessageAsync(box, messageId);
        Assert.Equal("pending", afterStale.Str("content_state")); // the stale result was discarded

        await handler(freshScope).HandleAsync(freshLease, CancellationToken.None);
        await handler(staleScope).HandleAsync(staleLease, CancellationToken.None); // a late retry changes nothing either

        var source = await Inbound.Client(box.Key).GetAsync($"{Messages}/{messageId}/source").ExpectAsync(HttpStatusCode.OK);
        Assert.Contains("second worker", source.Str("text"));
        await fx.WithDbAsync(async db =>
        {
            var job = await db.BackgroundJobs.SingleAsync(j => j.TargetId == Guid.Parse(messageId));
            Assert.Equal((BackgroundJobState.Succeeded, 1), (job.State, job.Generation));
            Assert.Null(job.LeaseToken);
        });
    }

    [Fact] // AC 6
    public async Task Stored_source_is_returned_safely_and_survives_a_restart_without_the_provider()
    {
        var box = await Inbound.CreateMailboxAsync();
        var emailId = InboundHarness.NewEmailId();
        const string html = "<script>alert('x')</script><img src=\"https://tracker.example/pixel.gif\"><p>Platba</p>";
        Inbound.Api.Respond(emailId, FakeResendApi.Ok(InboundHarness.Content(emailId, [box.Address], html: html)));
        var messageId = await DeliverEmailAsync(emailId, box.Address);
        await Inbound.RunJobsAsync();

        Assert.All(Inbound.Api.Requests.Where(r => r.EmailId == emailId), r => Assert.Equal($"Bearer {InboundHarness.ApiKey}", r.Authorization));

        var response = await Inbound.Client(box.Key).GetAsync($"{Messages}/{messageId}/source");
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Signature=", raw); // the provider's signed download URL is never returned
        Assert.DoesNotContain(InboundHarness.ApiKey, raw);

        var source = JsonNode.Parse(raw)!;
        Assert.Equal(html, source.Str("html")); // returned as text for escaped display, never rewritten or rendered
        Assert.Equal(("pass", "pass", "pass", "resend_receiving_api"), (source["sender_authentication"]!.Str("spf"),
            source["sender_authentication"]!.Str("dkim"), source["sender_authentication"]!.Str("dmarc"), source["sender_authentication"]!.Str("origin")));
        Assert.Equal("spf=pass (forged)", source["headers"]!.Str("authentication-results")); // sender headers stay separate
        Assert.Equal("vypis.pdf", source["attachments"]![0]!.Str("filename"));
        Assert.Equal("fixture", source.Str("source_kind"));
        Assert.Matches("^[0-9a-f]{64}$", source.Str("content_sha256"));

        var calls = Inbound.Api.Requests.Count;
        await using (var restarted = Inbound.CreateFactory())
        {
            var again = await fx.Client(box.Key, restarted).GetAsync($"{Messages}/{messageId}/source").ExpectAsync(HttpStatusCode.OK);
            Assert.Equal(source.ToJsonString(), again.ToJsonString());
        }

        Assert.Equal(calls, Inbound.Api.Requests.Count);

        // Oversized content is a visible terminal error, never a truncated message.
        var largeEmail = InboundHarness.NewEmailId();
        Inbound.Api.Respond(largeEmail,
            FakeResendApi.Ok(InboundHarness.Content(largeEmail, [box.Address], text: new string('x', 5 * 1024 * 1024))));
        var largeId = await DeliverEmailAsync(largeEmail, box.Address);
        await Inbound.RunJobsAsync();
        var large = await GetMessageAsync(box, largeId);
        Assert.Equal("failed", large.Str("content_state"));
        Assert.Equal("content_too_large", large["content_error"]!.Str("code"));
        Assert.Equal("Příchozí platba", large.Str("subject")); // metadata is retained
        await Inbound.Client(box.Key).GetAsync($"{Messages}/{largeId}/source").ExpectErrorAsync(HttpStatusCode.Conflict, "content_not_available");

        // Content naming different recipients than the verified event is held, never re-owned.
        var other = await Inbound.CreateMailboxAsync();
        var mismatchEmail = InboundHarness.NewEmailId();
        Inbound.Api.Respond(mismatchEmail, FakeResendApi.Ok(InboundHarness.Content(mismatchEmail, [other.Address])));
        var mismatchId = await DeliverEmailAsync(mismatchEmail, box.Address);
        await Inbound.RunJobsAsync();
        var mismatch = await GetMessageAsync(box, mismatchId);
        Assert.Equal(("available", "recipient_mismatch", false),
            (mismatch.Str("content_state"), mismatch.Str("hold_reason"), mismatch["source_available"]!.GetValue<bool>()));
        await Inbound.Client(box.Key).GetAsync($"{Messages}/{mismatchId}/source").ExpectErrorAsync(HttpStatusCode.Conflict, "content_withheld");
        Assert.Empty((await Inbound.Client(other.Key).GetAsync(Messages).ExpectAsync(HttpStatusCode.OK)).Items());
    }

    [Fact] // AC 7
    public async Task Parents_manage_direct_children_messages_and_intake_changes_no_payment()
    {
        var box = await Inbound.CreateMailboxAsync();
        var parent = Inbound.Client(fx.RootManagementKey);
        var childPath = $"/v1/subtenants/{box.TenantId}/inbound-messages";

        var payment = await Inbound.Client(box.Key).PostAsJsonAsync("/v1/tenant/payment-requests", new
        {
            bank_connection_id = box.ConnectionId,
            external_id = "booking_inbound",
            amount = 45000,
            currency = "CZK",
            variable_symbol = "2026000777",
        }).ExpectAsync(HttpStatusCode.Created);

        var emailId = InboundHarness.NewEmailId();
        Inbound.Api.Respond(emailId, FakeResendApi.Status(HttpStatusCode.Forbidden));
        var messageId = await DeliverEmailAsync(emailId, box.Address);
        await Inbound.RunJobsAsync();

        // The parent lists, reads, and retries its child's message with a management key.
        var listed = await parent.GetAsync($"{childPath}?content_state=failed&hold_reason=none").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(messageId, Assert.Single(listed.Items())!.Str("id"));
        Assert.Empty((await parent.GetAsync($"{childPath}?content_state=available").ExpectAsync(HttpStatusCode.OK)).Items());
        await parent.GetAsync($"{childPath}?content_state=parsed").ExpectErrorAsync(HttpStatusCode.BadRequest, "invalid_request");
        await parent.GetAsync($"{childPath}?received_after=2026-10-07").ExpectErrorAsync(HttpStatusCode.BadRequest, "invalid_request");
        Assert.Single((await parent.GetAsync($"{childPath}?received_after=2026-10-07T06:00:00Z&received_before=2026-10-07T08:00:00Z")
            .ExpectAsync(HttpStatusCode.OK)).Items());

        Inbound.Api.Respond(emailId, FakeResendApi.Ok(InboundHarness.Content(emailId, [box.Address])));
        await parent.PostAsync($"{childPath}/{messageId}/retry-fetch", null).ExpectAsync(HttpStatusCode.Accepted);
        await Inbound.RunJobsAsync();
        await parent.GetAsync($"{childPath}/{messageId}/source").ExpectAsync(HttpStatusCode.OK);

        // Tenant keys cannot use child routes; other roots and siblings see nothing.
        await Inbound.Client(fx.RootTenantKey).GetAsync(childPath).ExpectAsync(HttpStatusCode.Forbidden);
        var otherRoot = await fx.SetupRootAsync(PairrotFixture.Unique("root"));
        await Inbound.Client(otherRoot.ManagementKey!.Secret).GetAsync(childPath).ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");
        var sibling = await fx.CreateChildAsync();
        await parent.GetAsync($"/v1/subtenants/{sibling.Id}/inbound-messages/{messageId}").ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");

        // An inactive child's history stays readable to the parent, but retries need an active child.
        await fx.SetStatusAsync(box.TenantId, TenantStatus.Suspended);
        await parent.GetAsync($"{childPath}/{messageId}/source").ExpectAsync(HttpStatusCode.OK);
        await parent.PostAsync($"{childPath}/{messageId}/retry-fetch", null).ExpectErrorAsync(HttpStatusCode.Conflict, "tenant_not_active");
        await fx.SetStatusAsync(box.TenantId, TenantStatus.Active);

        // A browser session with the tenant selected sees the same, with antiforgery on the retry.
        // (The normal host: the Resend host's shifted clock would expire the session.)
        var inBrowser = await fx.SignInAsync(box.TenantId);
        var browserList = await inBrowser.GetAsync(Messages).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(messageId, Assert.Single(browserList.Items())!.Str("id"));
        await inBrowser.PostAsync($"{Messages}/{messageId}/retry-fetch").ExpectErrorAsync(HttpStatusCode.Conflict, "content_already_available");
        inBrowser.AntiforgeryToken = null;
        await inBrowser.PostAsync($"{Messages}/{messageId}/retry-fetch").ExpectErrorAsync(HttpStatusCode.BadRequest, "invalid_antiforgery_token");

        // Receiving and storing an email marks nothing as paid.
        var after = await Inbound.Client(box.Key).GetAsync($"/v1/tenant/payment-requests/{payment.Str("id")}").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(("open", 0, 45000, 1), (after.Str("status"), after["paid_amount"]!.GetValue<int>(),
            after["remaining_amount"]!.GetValue<int>(), after["version"]!.GetValue<int>()));
    }

    /// <summary>Delivers one signed event to <paramref name="recipients"/> and returns the new message's ID.</summary>
    private Task<string> DeliverAsync(params string[] recipients) => DeliverEmailAsync(InboundHarness.NewEmailId(), recipients);

    private async Task<string> DeliverEmailAsync(string emailId, params string[] recipients)
    {
        await Inbound.PostEventAsync(InboundHarness.ReceivedEvent(emailId, recipients)).ExpectAsync(HttpStatusCode.OK);
        Guid id = default;
        await fx.WithDbAsync(async db => id = await db.InboundMessages.Where(m => m.SourceMessageId == emailId).Select(m => m.Id).SingleAsync());
        return id.ToString();
    }

    private Task<JsonNode> GetMessageAsync(Mailbox box, string messageId) =>
        Inbound.Client(fx.RootManagementKey).GetAsync($"/v1/subtenants/{box.TenantId}/inbound-messages/{messageId}").ExpectAsync(HttpStatusCode.OK);
}
