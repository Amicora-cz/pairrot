using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pairrot.Core.Inbound;
using Pairrot.Core.Parsing;
using Pairrot.Core.Tenants;
using Pairrot.Infrastructure.Inbound;
using Pairrot.Tests.Shared;

namespace Pairrot.IntegrationTests;

/// <summary>
/// Feature 008: forwarding mailboxes entered by hand, valid forwarded notifications,
/// foreign mail that is never stored, and pausing an address that receives it. The forwarding
/// headers here are synthetic; real Gmail and Seznam samples are still required (criterion 1).
/// </summary>
[Collection(PairrotCollection.Name)]
public class ForwardingTests(PairrotFixture fx)
{
    private const string Connections = "/v1/tenant/bank-connections";
    private const string Messages = "/v1/tenant/inbound-messages";
    private const string Mailbox = "lektor@" + ParsingHarness.VerifiedForwarderDomain;

    private ParsingHarness P => fx.Parsing;

    private async Task<Account> NewAccountAsync()
    {
        var child = await fx.CreateChildAsync();
        return await P.CreateAccountAsync(child.Id, child.Key);
    }

    private string Sources(Account account) => $"{Connections}/{account.ConnectionId}/forwarding-sources";

    /// <summary>Enters <paramref name="address"/> as a forwarding mailbox of the account.</summary>
    private Task RegisterSourceAsync(Account account, string address) =>
        P.Client(account.Key).PostAsJsonAsync(Sources(account), new { address }).ExpectAsync(HttpStatusCode.Created);

    private async Task<JsonNode> ReceptionAsync(Account account) =>
        await P.Client(account.Key).GetAsync($"{Connections}/{account.ConnectionId}/reception").ExpectAsync(HttpStatusCode.OK);

    private async Task<JsonNode> LatestRunAsync(Account account, Guid messageId) =>
        (await P.Client(account.Key).GetAsync($"{Messages}/{messageId}/parse-runs").ExpectAsync(HttpStatusCode.OK))["runs"]!.AsArray()[^1]!;

    [Fact] // AC 5, 12
    public async Task A_forwarding_mailbox_is_entered_by_hand_and_active_at_once()
    {
        var account = await NewAccountAsync();
        var client = P.Client(account.Key);

        var created = await client.PostAsJsonAsync(Sources(account), new { address = "  Lektor@Mail.Pairrot-Fixture.net " })
            .ExpectAsync(HttpStatusCode.Created);
        Assert.Equal((Mailbox, "active"), (created.Str("address"), created.Str("state")));
        Assert.Equal("waiting_for_first_message", (await ReceptionAsync(account)).Str("setup_status"));

        // Entering it again returns the same mailbox.
        var again = await client.PostAsJsonAsync(Sources(account), new { address = Mailbox }).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(created.Str("id"), again.Str("id"));

        // The bank's own identity, Pairrot's address, and several addresses are not forwarding mailboxes.
        await client.PostAsJsonAsync(Sources(account), new { address = SyntheticBankProfile.Sender })
            .ExpectErrorAsync(HttpStatusCode.BadRequest, "invalid_forwarding_address");
        await client.PostAsJsonAsync(Sources(account), new { address = account.Address })
            .ExpectErrorAsync(HttpStatusCode.BadRequest, "invalid_forwarding_address");
        await client.PostAsJsonAsync(Sources(account), new { address = "a@b.cz, c@d.cz" })
            .ExpectErrorAsync(HttpStatusCode.BadRequest, "invalid_forwarding_address");

        await client.DeleteAsync($"{Sources(account)}/{created.Str("id")}").ExpectAsync(HttpStatusCode.NoContent);
        Assert.Empty((await client.GetAsync(Sources(account)).ExpectAsync(HttpStatusCode.OK))["items"]!.AsArray());
        await client.DeleteAsync($"{Sources(account)}/{created.Str("id")}").ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");

        await fx.WithDbAsync(async db =>
        {
            var actions = await db.AuditEvents.Where(e => e.TargetTenantId == account.TenantId).Select(e => e.Action).ToListAsync();
            Assert.Contains(AuditActions.ForwardingSourceRegistered, actions);
            Assert.Contains(AuditActions.ForwardingSourceRemoved, actions);
        });
    }

    [Fact] // AC 2, 3, 4
    public async Task Forwards_from_a_registered_mailbox_are_valid_and_others_are_not()
    {
        var account = await NewAccountAsync();
        await RegisterSourceAsync(account, Mailbox);
        var notice = SyntheticBankProfile.Notice(ParsingHarness.Incoming(account.Bank, reference: "FWD-1"));

        // Manual forward: From is the registered mailbox with DMARC pass; the notice is quoted.
        var quoted = "Posílám platbu.\n---------- Forwarded message ---------\nFrom: Banka <notify@bank.pairrot-fixture.net>\nSubject: Notice\n" + string.Join('\n', notice.Split('\n').Select(l => "> " + l));
        var manual = await P.DeliverAsync(ParsingHarness.Delivery(account, quoted, from: $"Lektor <{Mailbox}>"));
        var manualRun = await LatestRunAsync(account, manual);
        Assert.Equal(("parsed", "trusted"), (manualRun.Str("outcome"), manualRun.Str("trust_decision")));
        Assert.Equal(("manual_forward", Mailbox, "source_dmarc"), (manualRun["trust"]!["evidence"]!.Str("origin_path"),
            manualRun["trust"]!["evidence"]!.Str("forwarding_source"), manualRun["trust"]!["evidence"]!.Str("authenticated_by")));
        var tx = Assert.Single((await P.Client(account.Key).GetAsync("/v1/tenant/transactions").ExpectAsync(HttpStatusCode.OK)).Items())!;
        Assert.Equal(("accepted", "manual_forward"), (tx.Str("ingestion_status"), tx.Str("origin_path")));

        var reception = await ReceptionAsync(account);
        Assert.Equal("bank_message_trusted", reception.Str("setup_status"));
        Assert.Equal("manual_forward", reception["latest_bank_message"]!.Str("origin_path"));

        // Automatic forward that broke the bank's DMARC, authenticated by SPF of the forwarding mailbox.
        var automatic = await P.DeliverAsync(ParsingHarness.Delivery(account,
                SyntheticBankProfile.Notice(ParsingHarness.Incoming(account.Bank, reference: "FWD-2")),
                authentication: new SenderAuthentication("pass", "fail", "fail", "smtp_receiving_server", null))
            with { EnvelopeSender = $"lektor+caf_=x=smtp.pairrot-fixture.net@{ParsingHarness.VerifiedForwarderDomain}" });
        var automaticRun = await LatestRunAsync(account, automatic);
        Assert.Equal(("parsed", "automatic_forward", "envelope_spf"), (automaticRun.Str("outcome"),
            automaticRun["trust"]!["evidence"]!.Str("origin_path"), automaticRun["trust"]!["evidence"]!.Str("authenticated_by")));

        // Not valid: DMARC fails for the mailbox, a provider not verified for SPF, failed SPF, a mailbox that was not entered.
        var impersonated = await P.DeliverAsync(ParsingHarness.Delivery(account, quoted.Replace("FWD-1", "FWD-3"), from: Mailbox,
            authentication: new SenderAuthentication("fail", "fail", "fail", "smtp_receiving_server", null)));
        Assert.Equal(("review_required", "untrusted"), ((await LatestRunAsync(account, impersonated)).Str("outcome"),
            (await LatestRunAsync(account, impersonated)).Str("trust_decision")));

        const string unverified = "lektor@unverified.pairrot-fixture.net";
        await RegisterSourceAsync(account, unverified);
        var unverifiedForward = await P.DeliverAsync(ParsingHarness.Delivery(account,
                SyntheticBankProfile.Notice(ParsingHarness.Incoming(account.Bank, reference: "FWD-4")),
                authentication: new SenderAuthentication("pass", "fail", "fail", "smtp_receiving_server", null))
            with { EnvelopeSender = unverified });
        var unverifiedRun = await LatestRunAsync(account, unverifiedForward);
        Assert.Equal("review_required", unverifiedRun.Str("outcome"));
        Assert.Contains(unverifiedRun["reasons"]!.AsArray(), r => r!.Str("code") == ParseReasons.ForwarderNotAuthenticated);

        var spfFailed = await P.DeliverAsync(ParsingHarness.Delivery(account,
                SyntheticBankProfile.Notice(ParsingHarness.Incoming(account.Bank, reference: "FWD-5")),
                authentication: new SenderAuthentication("fail", "fail", "fail", "smtp_receiving_server", null))
            with { EnvelopeSender = Mailbox });
        Assert.Equal("review_required", (await LatestRunAsync(account, spfFailed)).Str("outcome"));

        var unregistered = await P.AcceptAsync(ParsingHarness.Delivery(account, quoted.Replace("FWD-1", "FWD-6"), from: "cizi@" + ParsingHarness.VerifiedForwarderDomain));
        await fx.WithDbAsync(async db => Assert.Equal(InboundSenderClass.Foreign,
            (await db.InboundMessages.SingleAsync(m => m.Id == unregistered.MessageId)).SenderClass));

        Assert.Equal(["FWD-1", "FWD-2"], (await P.TransactionsAsync(account.ConnectionId))
            .Where(t => t.IngestionStatus == Core.Transactions.IngestionStatus.Accepted).Select(t => t.BankTransactionId).Order());
    }

    [Fact] // AC 7, 8, 9, 11, 13
    public async Task Foreign_mail_is_not_stored_and_too_much_of_it_pauses_the_address()
    {
        var account = await NewAccountAsync();
        var client = P.Client(account.Key);
        var foreign = Enumerable.Range(0, 5).Select(i => ParsingHarness.Delivery(account, $"Newsletter {i}", from: $"news{i}@shop.example")).ToList();
        foreach (var delivery in foreign)
        {
            await P.AcceptAsync(delivery);
        }

        // A repeated delivery of one foreign message counts once.
        await P.AcceptAsync(foreign[0] with { SourceDeliveryId = $"d_{Guid.NewGuid():N}" });
        await P.RunJobsAsync();

        await fx.WithDbAsync(async db =>
        {
            var stored = await db.InboundMessages.AsNoTracking().Where(m => m.BankConnectionId == account.ConnectionId).ToListAsync();
            Assert.Equal(5, stored.Count);
            Assert.All(stored, m =>
            {
                Assert.Equal((InboundSenderClass.Foreign, InboundContentState.Skipped, "shop.example"), (m.SenderClass, m.ContentState, m.SenderDomain));
                Assert.Null(m.Subject);
                Assert.Null(m.HeaderFrom);
                Assert.Null(m.TextBody);
                Assert.NotNull(m.RetainUntil);
            });
            var receipts = await db.InboundDeliveryReceipts.Where(r => stored.Select(m => m.Id).Contains(r.InboundMessageId)).ToListAsync();
            Assert.All(receipts, r => Assert.Null(r.RawPayload));
        });
        Assert.Empty((await client.GetAsync(Messages).ExpectAsync(HttpStatusCode.OK)).Items());
        var reception = await ReceptionAsync(account);
        Assert.Equal(("waiting_for_first_message", 5, "active"), (reception.Str("setup_status"),
            reception["foreign_messages_in_window"]!.GetValue<int>(), reception.Str("address_processing")));
        Assert.Equal(["shop.example"], reception["recent_foreign_domains"]!.AsArray().Select(d => d!.GetValue<string>()));

        // The sixth pauses the address; even a bank notification is then not stored.
        await P.AcceptAsync(ParsingHarness.Delivery(account, "Newsletter 6", from: "news6@shop.example"));
        reception = await ReceptionAsync(account);
        Assert.Equal(("paused_foreign_mail", "paused_foreign_mail", false), (reception.Str("setup_status"),
            reception.Str("address_processing"), reception["operator_resume_required"]!.GetValue<bool>()));
        var held = await P.AcceptAsync(ParsingHarness.Delivery(account, SyntheticBankProfile.Notice(ParsingHarness.Incoming(account.Bank))));
        await P.RunJobsAsync();
        await fx.WithDbAsync(async db =>
        {
            var message = await db.InboundMessages.SingleAsync(m => m.Id == held.MessageId);
            Assert.Equal((InboundHoldReason.AddressPaused, InboundContentState.Skipped), (message.HoldReason, message.ContentState));
            Assert.True(await db.AuditEvents.AnyAsync(e => e.TargetTenantId == account.TenantId && e.Action == AuditActions.InboundAddressPaused));
        });
        Assert.Empty(await P.TransactionsAsync(account.ConnectionId));

        // The tenant resumes; a later bank notification is processed. Nothing received while paused is retrieved.
        var resumed = await client.PostAsync($"{Connections}/{account.ConnectionId}/inbound-address/resume", null).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("active", resumed.Str("address_processing"));
        await client.PostAsync($"{Connections}/{account.ConnectionId}/inbound-address/resume", null)
            .ExpectErrorAsync(HttpStatusCode.Conflict, "address_not_paused");
        await P.DeliverAsync(account, ParsingHarness.Incoming(account.Bank, reference: "AFTER"));
        Assert.Equal("AFTER", Assert.Single(await P.TransactionsAsync(account.ConnectionId)).BankTransactionId);

        // Paused again soon after a resume: only an operator may resume, or the tenant replaces the address.
        await fx.WithDbAsync(db => db.InboundMessages.Where(m => m.BankConnectionId == account.ConnectionId && m.SenderClass == InboundSenderClass.Foreign)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.CreatedAt, m => m.CreatedAt.AddDays(-2))));
        for (var i = 0; i < 6; i++)
        {
            await P.AcceptAsync(ParsingHarness.Delivery(account, $"Spam {i}", from: $"spam{i}@spam.example"));
        }

        reception = await ReceptionAsync(account);
        Assert.Equal(("paused_foreign_mail", true), (reception.Str("setup_status"), reception["operator_resume_required"]!.GetValue<bool>()));
        await client.PostAsync($"{Connections}/{account.ConnectionId}/inbound-address/resume", null)
            .ExpectErrorAsync(HttpStatusCode.Conflict, "operator_resume_required");

        var replaced = await client.PostAsync($"{Connections}/{account.ConnectionId}/inbound-address", null).ExpectAsync(HttpStatusCode.OK);
        Assert.True(replaced["address_replaced"]!.GetValue<bool>());
        Assert.NotEqual(account.Address, replaced.Str("inbound_address"));
        Assert.Equal("active", (await ReceptionAsync(account)).Str("address_processing"));
        await fx.WithDbAsync(async db =>
        {
            var old = await db.InboundAddresses.SingleAsync(a => a.Address == account.Address);
            Assert.Equal((InboundAddressState.Retired, InboundAddressProcessing.PausedForeignMail), (old.State, old.Processing));
        });

        // An operator can still resume the old address.
        var resumedByOperator = await P.WithServicesAsync(s => s.GetRequiredService<ForwardingService>().ResumeAsOperatorAsync(account.Address, "test"));
        Assert.Null(resumedByOperator.Error);
        Assert.Contains(await P.WithServicesAsync(s => s.GetRequiredService<ForwardingService>().DailyVolumeAsync(1)), v => v.Foreign >= 6);
    }

    [Fact] // AC 10
    public async Task Forwarding_confirmations_are_shown_as_text_never_parsed_and_expire()
    {
        var account = await NewAccountAsync();
        var confirmation = await P.DeliverAsync(ParsingHarness.Delivery(account,
            "lektor@gmail.com has requested to automatically forward mail to your email address.\nConfirmation code: 123456789\nhttps://mail.google.com/mail/vf-...",
            from: "Gmail Team <forwarding-noreply@google.com>"));

        var reception = await ReceptionAsync(account);
        Assert.Equal("confirmation_received", reception.Str("setup_status"));
        Assert.Equal(("123456789", confirmation.ToString()), (reception["confirmation"]!.Str("code"), reception["confirmation"]!.Str("message_id")));
        await fx.WithDbAsync(async db =>
        {
            Assert.False(await db.MessageParseRuns.AnyAsync(r => r.InboundMessageId == confirmation));
            Assert.Equal(InboundSenderClass.ForwardingConfirmation, (await db.InboundMessages.SingleAsync(m => m.Id == confirmation)).SenderClass);
        });
        await P.Client(account.Key).PostAsync($"{Messages}/{confirmation}/reprocess", null)
            .ExpectErrorAsync(HttpStatusCode.Conflict, "not_a_bank_message");

        // Expired confirmations and foreign metadata are deleted; bank messages are kept.
        var foreign = await P.AcceptAsync(ParsingHarness.Delivery(account, "Ad", from: "ad@shop.example"));
        var bank = await P.DeliverAsync(account, ParsingHarness.Incoming(account.Bank, reference: "KEEP"));
        await fx.WithDbAsync(db => db.InboundMessages.Where(m => m.Id == confirmation || m.Id == foreign.MessageId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.RetainUntil, DateTimeOffset.UtcNow.AddDays(-1))));
        Assert.True(await P.WithServicesAsync(s => s.GetRequiredService<InboundRetention>().PurgeExpiredAsync()) >= 2);
        await fx.WithDbAsync(async db =>
        {
            Assert.False(await db.InboundMessages.AnyAsync(m => m.Id == confirmation || m.Id == foreign.MessageId));
            Assert.False(await db.InboundDeliveryReceipts.AnyAsync(r => r.InboundMessageId == confirmation || r.InboundMessageId == foreign.MessageId));
            Assert.True(await db.InboundMessages.AnyAsync(m => m.Id == bank));
        });
    }

    [Fact] // AC 12
    public async Task Forwarding_routes_follow_tenant_and_parent_permissions()
    {
        var account = await NewAccountAsync();
        var stranger = P.Client((await fx.CreateChildAsync()).Key);
        var path = $"{Connections}/{account.ConnectionId}";

        await stranger.GetAsync($"{path}/forwarding-sources").ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");
        await stranger.PostAsJsonAsync($"{path}/forwarding-sources", new { address = Mailbox }).ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");
        await stranger.GetAsync($"{path}/reception").ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");
        await stranger.PostAsync($"{path}/inbound-address/resume", null).ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");

        var parent = P.Client(fx.RootManagementKey);
        var child = $"/v1/subtenants/{account.TenantId}/bank-connections/{account.ConnectionId}";
        await parent.PostAsJsonAsync($"{child}/forwarding-sources", new { address = Mailbox }).ExpectAsync(HttpStatusCode.Created);
        Assert.Single((await parent.GetAsync($"{child}/forwarding-sources").ExpectAsync(HttpStatusCode.OK))["items"]!.AsArray());
        Assert.Equal("waiting_for_first_message", (await parent.GetAsync($"{child}/reception").ExpectAsync(HttpStatusCode.OK)).Str("setup_status"));

        await fx.SetStatusAsync(account.TenantId, TenantStatus.Suspended);
        try
        {
            await parent.GetAsync($"{child}/reception").ExpectAsync(HttpStatusCode.OK);
            await parent.PostAsJsonAsync($"{child}/forwarding-sources", new { address = "x@" + ParsingHarness.VerifiedForwarderDomain })
                .ExpectErrorAsync(HttpStatusCode.Conflict, "tenant_not_active");
        }
        finally
        {
            await fx.SetStatusAsync(account.TenantId, TenantStatus.Active);
        }
    }
}
