using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Pairrot.Core.Inbound;
using Pairrot.Core.Jobs;
using Pairrot.Infrastructure.Inbound;

namespace Pairrot.IntegrationTests;

/// <summary>
/// The provider-independent intake contract (Feature 006 criteria 9 and 10): fake deferred and
/// inline adapters go through the same common intake as Resend, without any Resend type.
/// </summary>
[Collection(PairrotCollection.Name)]
public class InboundContractTests(PairrotFixture fx)
{
    private const string Messages = "/v1/tenant/inbound-messages";
    private const string Connections = "/v1/tenant/bank-connections";

    private InboundHarness Inbound => fx.Inbound;

    private static readonly SenderAuthentication Passing = new("pass", "pass", "pass", "fake_receiving_server", """{"spf":"pass","dkim":"pass","dmarc":"pass"}""");

    private static InboundContentSnapshot Snapshot(SenderAuthentication? authentication = null) => new(
        "Na účet přišla platba 450,00 CZK.",
        "<p>Na účet přišla <b>platba</b></p>",
        """{"from":"Česká spořitelna <info@csas.cz>","authentication-results":"spf=pass (forged)"}""",
        null,
        """[{"filename":"vypis.pdf","content_type":"application/pdf","size":1234}]""",
        authentication ?? Passing);

    [Fact] // AC 9
    public async Task Inline_and_deferred_deliveries_store_the_same_common_source()
    {
        var child = await fx.CreateChildAsync();
        var deferredAddress = await BindAsync(child.Id, child.Key, InboundHarness.DeferredInstanceId, "deferred.pairrot.test");
        var inlineAddress = await BindAsync(child.Id, child.Key, InboundHarness.InlineInstanceId, "inline.pairrot.test");

        var reference = $"ref_{Guid.NewGuid():N}";
        Inbound.Sources.Set(reference, new ContentRetrieval(ContentRetrievalOutcome.Retrieved, Snapshot()));
        var deferred = await Inbound.AcceptAsync(Delivery(InboundHarness.DeferredInstanceId, deferredAddress) with { ContentReference = reference });
        var inline = await Inbound.AcceptAsync(Delivery(InboundHarness.InlineInstanceId, inlineAddress) with { Content = Snapshot() });
        Assert.Equal((IntakeStatus.Accepted, InboundRoutingState.Routed), (deferred.Status, deferred.Routing));
        Assert.Equal((IntakeStatus.Accepted, InboundRoutingState.Routed), (inline.Status, inline.Routing));

        // Inline content is available at once, without a job; deferred content after retrieval.
        await fx.WithDbAsync(async db =>
        {
            Assert.Equal(InboundContentState.Available, (await db.InboundMessages.SingleAsync(m => m.Id == inline.MessageId)).ContentState);
            Assert.False(await db.BackgroundJobs.AnyAsync(j => j.Type == BackgroundJobTypes.FetchInboundContent && j.TargetId == inline.MessageId));
            Assert.Equal(1, await db.BackgroundJobs.CountAsync(j => j.Type == BackgroundJobTypes.FetchInboundContent && j.TargetId == deferred.MessageId));
        });
        await Inbound.RunJobsAsync();
        Assert.Contains($"{InboundHarness.DeferredInstanceId}/{reference}", Inbound.Sources.Retrieved);

        var client = Inbound.Client(child.Key);
        var deferredSource = await client.GetAsync($"{Messages}/{deferred.MessageId}/source").ExpectAsync(HttpStatusCode.OK);
        var inlineSource = await client.GetAsync($"{Messages}/{inline.MessageId}/source").ExpectAsync(HttpStatusCode.OK);
        foreach (var field in new[] { "text", "html", "headers", "attachments", "sender_authentication", "content_sha256", "subject", "header_from" })
        {
            Assert.Equal(deferredSource[field]!.ToJsonString(), inlineSource[field]!.ToJsonString());
        }

        var deferredMessage = await client.GetAsync($"{Messages}/{deferred.MessageId}").ExpectAsync(HttpStatusCode.OK);
        var inlineMessage = await client.GetAsync($"{Messages}/{inline.MessageId}").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(("deferred", 1), (deferredMessage.Str("content_mode"), deferredMessage["retrievals"]!.AsArray().Count));
        Assert.Equal(("inline", 0), (inlineMessage.Str("content_mode"), inlineMessage["retrievals"]!.AsArray().Count));
        Assert.Equal(("fixture", "test"), (inlineMessage.Str("source_kind"), inlineMessage.Str("environment")));

        // Missing authentication evidence stays unknown in both modes.
        var unknownReference = $"ref_{Guid.NewGuid():N}";
        Inbound.Sources.Set(unknownReference, new ContentRetrieval(ContentRetrievalOutcome.Retrieved, Snapshot(SenderAuthentication.Unknown)));
        var deferredUnknown = await Inbound.AcceptAsync(Delivery(InboundHarness.DeferredInstanceId, deferredAddress) with { ContentReference = unknownReference });
        var inlineUnknown = await Inbound.AcceptAsync(Delivery(InboundHarness.InlineInstanceId, inlineAddress) with
        {
            Content = Snapshot(new SenderAuthentication("PASSED", "", "pass?", null, null)),
        });
        await Inbound.RunJobsAsync();
        foreach (var id in new[] { deferredUnknown.MessageId, inlineUnknown.MessageId })
        {
            var source = await client.GetAsync($"{Messages}/{id}/source").ExpectAsync(HttpStatusCode.OK);
            var evidence = source["sender_authentication"]!;
            Assert.Equal(("unknown", "unknown", "unknown"), (evidence.Str("spf"), evidence.Str("dkim"), evidence.Str("dmarc")));
        }

        // Inline content is never "retrieved again".
        var tooLarge = await Inbound.AcceptAsync(Delivery(InboundHarness.InlineInstanceId, inlineAddress) with
        {
            Content = Snapshot() with { Text = new string('x', 5 * 1024 * 1024 + 1) },
        });
        var failed = await client.GetAsync($"{Messages}/{tooLarge.MessageId}").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(("failed", "content_too_large"), (failed.Str("content_state"), failed["content_error"]!.Str("code")));
        await client.PostAsync($"{Messages}/{tooLarge.MessageId}/retry-fetch", null)
            .ExpectErrorAsync(HttpStatusCode.Conflict, "content_fetch_not_supported");
    }

    [Fact] // AC 9: the same dedup, quarantine, and contract checks for every adapter
    public async Task Common_intake_enforces_the_contract_for_any_adapter()
    {
        var child = await fx.CreateChildAsync();
        var address = await BindAsync(child.Id, child.Key, InboundHarness.InlineInstanceId, "inline.pairrot.test");
        var delivery = Delivery(InboundHarness.InlineInstanceId, address) with { Content = Snapshot() };

        Assert.Equal(IntakeStatus.Accepted, (await Inbound.AcceptAsync(delivery)).Status);
        Assert.Equal(IntakeStatus.Duplicate, (await Inbound.AcceptAsync(delivery)).Status);
        Assert.Equal(IntakeStatus.Conflict, (await Inbound.AcceptAsync(delivery with { Subject = "changed" })).Status);

        // A second delivery of the same message keeps one message.
        var redelivered = await Inbound.AcceptAsync(delivery with { SourceDeliveryId = $"d_{Guid.NewGuid():N}" });
        Assert.Equal(IntakeStatus.Accepted, redelivered.Status);
        await fx.WithDbAsync(async db => Assert.Equal(1, await db.InboundMessages.CountAsync(m =>
            m.ProviderInstanceId == InboundHarness.InlineInstanceId && m.SourceMessageId == delivery.SourceMessageId)));

        // Contract violations are refused before anything is saved.
        Assert.Equal(IntakeStatus.Rejected, (await Inbound.AcceptAsync(Delivery(InboundHarness.InlineInstanceId, address))).Status);
        Assert.Equal(IntakeStatus.Rejected, (await Inbound.AcceptAsync(delivery with { SourceDeliveryId = "x2", ContentReference = "both" })).Status);
        Assert.Equal(IntakeStatus.Rejected, (await Inbound.AcceptAsync(delivery with { ProviderInstanceId = "nobody", SourceDeliveryId = "x3" })).Status);
        Assert.Equal(IntakeStatus.Rejected, (await Inbound.AcceptAsync(Delivery(InboundHarness.InlineInstanceId, address) with
        {
            Content = Snapshot() with { HeadersJson = "[]" },
        })).Status);

        // Without recipient evidence, and with an address claimed through the wrong instance, mail is quarantined with its content.
        var missing = await Inbound.AcceptAsync(Delivery(InboundHarness.InlineInstanceId) with { Content = Snapshot() });
        var claimed = await Inbound.AcceptAsync(Delivery(InboundHarness.DeferredInstanceId, address) with { ContentReference = "r" });
        Assert.Equal(InboundRoutingState.MissingRecipientEvidence, missing.Routing);
        Assert.Equal(InboundRoutingState.InstanceMismatch, claimed.Routing);
        await fx.WithDbAsync(async db =>
        {
            var quarantined = await db.InboundMessages.SingleAsync(m => m.Id == missing.MessageId);
            Assert.Equal((InboundContentState.Available, (Guid?)null), (quarantined.ContentState, quarantined.TenantId));
            Assert.False(await db.BackgroundJobs.AnyAsync(j => j.Type == BackgroundJobTypes.FetchInboundContent && j.TargetId == claimed.MessageId));
        });
        var visible = await Inbound.Client(child.Key).GetAsync(Messages).ExpectAsync(HttpStatusCode.OK);
        Assert.DoesNotContain(visible.Items(), m => m!.Str("id") == missing.MessageId.ToString() || m!.Str("id") == claimed.MessageId.ToString());

        // Inline content whose recipients contradict the delivery is held, not re-owned.
        var other = await BindAsync(child.Id, child.Key, InboundHarness.InlineInstanceId, "inline.pairrot.test");
        var contradicted = await Inbound.AcceptAsync(Delivery(InboundHarness.InlineInstanceId, address) with
        {
            Content = Snapshot() with { DeliveredRecipients = [other] },
        });
        await fx.WithDbAsync(async db => Assert.Equal(InboundHoldReason.RecipientMismatch,
            (await db.InboundMessages.SingleAsync(m => m.Id == contradicted.MessageId)).HoldReason));
    }

    [Fact] // AC 10
    public async Task Changing_the_default_provider_keeps_existing_addresses_and_queued_retrievals()
    {
        var box = await Inbound.CreateMailboxAsync();
        var emailId = InboundHarness.NewEmailId();
        Inbound.Api.Respond(emailId, FakeResendApi.Ok(InboundHarness.Content(emailId, [box.Address])));
        await Inbound.PostEventAsync(InboundHarness.ReceivedEvent(emailId, box.Address)).ExpectAsync(HttpStatusCode.OK);

        // The second Resend account becomes the default and the first is disabled for new mail.
        await using var switched = fx.Inbound.CreateFactory(defaultInstance: InboundHarness.SecondInstanceId).WithWebHostBuilder(builder =>
            builder.UseSetting($"InboundEmail:Instances:{InboundHarness.InstanceId}:Enabled", "false"));
        var client = fx.Client(box.Key, switched);

        var kept = await client.PostAsync($"{Connections}/{box.ConnectionId}/inbound-address", null).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal((box.Address, InboundHarness.InstanceId, false),
            (kept.Str("inbound_address"), kept.Str("provider_instance_id"), kept["address_replaced"]!.GetValue<bool>()));

        var created = await client.PostAsJsonAsync(Connections, new { account_number = BankConnectionTests.NewAccount().AccountNumber })
            .ExpectAsync(HttpStatusCode.Created);
        var second = await client.PostAsync($"{Connections}/{created.Str("id")}/inbound-address", null).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(InboundHarness.SecondInstanceId, second.Str("provider_instance_id"));
        Assert.EndsWith($"@{InboundHarness.SecondDomain}", second.Str("inbound_address"));

        // The queued retrieval still uses the account that received the email.
        await Inbound.RunJobsAsync(switched);
        Assert.All(Inbound.Api.Requests.Where(r => r.EmailId == emailId), r => Assert.Equal("resend.fake", r.Host));
        var messages = (await client.GetAsync(Messages).ExpectAsync(HttpStatusCode.OK)).Items();
        Assert.Equal("available", Assert.Single(messages)!.Str("content_state"));

        // The disabled instance accepts no new mail; the new one verifies with its own secret.
        await Inbound.PostEventAsync(InboundHarness.ReceivedEvent(InboundHarness.NewEmailId(), box.Address), factory: switched)
            .ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");

        // The same provider email ID in another account is a different message.
        await Inbound.PostEventAsync(InboundHarness.ReceivedEvent(emailId, second.Str("inbound_address")),
            secret: InboundHarness.SecondSecret, factory: switched, instance: InboundHarness.SecondInstanceId).ExpectAsync(HttpStatusCode.OK);
        await fx.WithDbAsync(async db =>
        {
            var both = await db.InboundMessages.Where(m => m.SourceMessageId == emailId).ToListAsync();
            Assert.Equal([InboundHarness.SecondInstanceId, InboundHarness.InstanceId], both.Select(m => m.ProviderInstanceId).Order());
            Assert.Equal(2, both.Select(m => m.BankConnectionId).Distinct().Count());
        });
    }

    [Fact] // Address provisioning for adapters that need remote setup
    public async Task Addresses_needing_remote_setup_stay_hidden_until_ready_and_are_retried_on_one_reservation()
    {
        await using var remote = fx.Inbound.CreateFactory(defaultInstance: InboundHarness.RemoteInstanceId);
        var child = await fx.CreateChildAsync();
        var client = fx.Client(child.Key, remote);
        var connection = await client.PostAsJsonAsync(Connections, new { account_number = BankConnectionTests.NewAccount().AccountNumber })
            .ExpectAsync(HttpStatusCode.Created);
        var path = $"{Connections}/{connection.Str("id")}/inbound-address";

        Inbound.Remote.Results.Enqueue(new ProvisioningResult(false, "The receiving server rejected the address."));
        var failed = await client.PostAsync(path, null).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(("failed", "The receiving server rejected the address."), (failed.Str("address_status"), failed.Str("provisioning_error")));
        Assert.Null(failed["inbound_address"]);

        var ready = await client.PostAsync(path, null).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("live_created", ready.Str("address_status"));
        Assert.Null(ready["provisioning_error"]);

        await fx.WithDbAsync(async db =>
        {
            var reservation = await db.InboundAddresses.SingleAsync(a => a.TenantId == child.Id);
            Assert.Equal(reservation.Address, ready.Str("inbound_address"));
            Assert.Equal(2, Inbound.Remote.Calls.Count(a => a == reservation.Address)); // retried on the same reservation
        });
    }

    private static InboundDelivery Delivery(string instance, params string[] recipients) => new()
    {
        ProviderInstanceId = instance,
        SourceDeliveryId = $"d_{Guid.NewGuid():N}",
        SourceMessageId = $"m_{Guid.NewGuid():N}",
        ReceivedAt = DateTimeOffset.Parse("2026-10-07T07:00:00Z"),
        EnvelopeRecipients = recipients,
        RecipientEvidence = "fake:envelope",
        EnvelopeSender = "bounce@csas.cz",
        HeaderFrom = "Česká spořitelna <info@csas.cz>",
        Subject = "Příchozí platba",
        TransportKind = "fake_transport",
        DeliveryEvidence = "test_harness",
    };

    /// <summary>A new connection of the tenant with a ready address bound to <paramref name="instance"/>.</summary>
    private async Task<string> BindAsync(Guid tenantId, string key, string instance, string domain)
    {
        var connection = await Inbound.Client(key).PostAsJsonAsync(Connections, new { account_number = BankConnectionTests.NewAccount().AccountNumber })
            .ExpectAsync(HttpStatusCode.Created);
        var address = $"p_{Guid.NewGuid():N}"[..26] + "@" + domain;
        await fx.WithDbAsync(async db =>
        {
            db.InboundAddresses.Add(new InboundAddress
            {
                Id = Guid.CreateVersion7(), TenantId = tenantId, BankConnectionId = Guid.Parse(connection.Str("id")), Address = address,
                ProviderInstanceId = instance, ProviderKind = FakeContentSource.FakeKind, Environment = InboundEnvironment.Test,
                ProvisioningState = InboundProvisioningState.Ready, State = InboundAddressState.Active,
                CreatedAt = DateTimeOffset.UtcNow, ReadyAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        });
        return address;
    }
}
