using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pairrot.Core.Banking;
using Pairrot.Core.Inbound;
using Pairrot.Core.Jobs;
using Pairrot.Core.Parsing;
using Pairrot.Core.Tenants;
using Pairrot.Core.Transactions;
using Pairrot.Infrastructure.Jobs;
using Pairrot.Infrastructure.Transactions;
using Pairrot.Tests.Shared;

namespace Pairrot.IntegrationTests;

/// <summary>
/// Feature 007 end to end with the synthetic test profile: parse jobs, trust, duplicates,
/// reprocessing, permissions, and provider independence. Real Česká spořitelna support is not
/// tested here because no verified sample exists yet (criteria 1 and 12 stay open).
/// </summary>
[Collection(PairrotCollection.Name)]
public class BankParsingTests(PairrotFixture fx)
{
    private const string Transactions = "/v1/tenant/transactions";
    private const string Messages = "/v1/tenant/inbound-messages";

    private ParsingHarness P => fx.Parsing;

    private async Task<Account> NewAccountAsync(string instance = ParsingHarness.SmtpInstanceId)
    {
        var child = await fx.CreateChildAsync();
        return await P.CreateAccountAsync(child.Id, child.Key, instance);
    }

    private async Task<JsonNode> RunsAsync(Account account, Guid messageId) =>
        await P.Client(account.Key).GetAsync($"{Messages}/{messageId}/parse-runs").ExpectAsync(HttpStatusCode.OK);

    [Fact] // AC 1, 2, 6, 11
    public async Task A_trusted_notice_records_one_transaction_and_changes_no_payment_request()
    {
        var account = await NewAccountAsync();
        var client = P.Client(account.Key);
        var request = await client.PostAsJsonAsync("/v1/tenant/payment-requests", new
        {
            bank_connection_id = account.ConnectionId, external_id = "lesson-1", amount = 45000, currency = "CZK", variable_symbol = "2026000123",
        }).ExpectAsync(HttpStatusCode.Created);

        var delivery = ParsingHarness.Delivery(account, SyntheticBankProfile.Notice(ParsingHarness.Incoming(account.Bank)));
        var messageId = await P.DeliverAsync(delivery);

        var tx = Assert.Single((await client.GetAsync(Transactions).ExpectAsync(HttpStatusCode.OK)).Items())!;
        Assert.Equal(45000, tx["amount"]!.GetValue<long>());
        Assert.Equal(("CZK", "2026000123", "2026-10-07"), (tx.Str("currency"), tx.Str("variable_symbol"), tx.Str("booked_on")));
        Assert.Equal(("accepted", "live"), (tx.Str("ingestion_status"), tx.Str("evidence")));
        Assert.Equal((SyntheticBankProfile.ProfileName, "1.0.0"), (tx.Str("bank_profile"), tx.Str("parser_version")));
        Assert.Equal(("19-2000145399/0100", "Jana Nováková"), (tx.Str("sender_account"), tx.Str("sender_name")));
        Assert.Equal(DateTimeOffset.Parse("2026-10-07T07:15:00Z"), DateTimeOffset.Parse(tx.Str("occurred_at")));
        Assert.Equal(DateTimeOffset.Parse("2026-10-07T07:16:00Z"), DateTimeOffset.Parse(tx.Str("received_at")));

        var detail = await client.GetAsync($"{Transactions}/{tx.Str("id")}").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(messageId.ToString(), Assert.Single(detail["sources"]!.AsArray())!.Str("inbound_message_id"));

        var message = await client.GetAsync($"{Messages}/{messageId}").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(("available", "parsed"), (message.Str("content_state"), message.Str("latest_parse_outcome")));

        var history = await RunsAsync(account, messageId);
        var run = Assert.Single(history["runs"]!.AsArray())!;
        Assert.Equal(("parsed", "parsed", "trusted"), (run.Str("outcome"), run.Str("reason_code"), run.Str("trust_decision")));
        Assert.Equal(("synthetic_v1", "text", tx.Str("id")), (run.Str("template_id"), run.Str("source_part"), run.Str("transaction_id")));
        Assert.Equal(45000, run["candidate"]!["amount"]!.GetValue<long>());
        Assert.Equal("smtp_receiving_server", run["trust"]!["evidence"]!.Str("authentication_origin"));
        Assert.Equal("succeeded", Assert.Single(history["jobs"]!.AsArray())!.Str("state"));

        // A repeated delivery of the same email changes nothing.
        var again = await P.AcceptAsync(delivery with { SourceDeliveryId = $"d_{Guid.NewGuid():N}" });
        Assert.Equal(messageId, again.MessageId);
        await P.RunJobsAsync();
        Assert.Single(await P.TransactionsAsync(account.ConnectionId));
        Assert.Single((await RunsAsync(account, messageId))["runs"]!.AsArray());

        // Recording a transaction marks no payment request paid.
        var unchanged = await client.GetAsync($"/v1/tenant/payment-requests/{request.Str("id")}").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(("open", 0L), (unchanged.Str("status"), unchanged["paid_amount"]!.GetValue<long>()));

        // Filters.
        async Task<int> Count(string query) => (await client.GetAsync($"{Transactions}?{query}").ExpectAsync(HttpStatusCode.OK)).Items().Count;
        Assert.Equal(1, await Count("variable_symbol=2026000123"));
        Assert.Equal(1, await Count($"bank_connection_id={account.ConnectionId}&amount=45000&booked_from=2026-10-07&booked_to=2026-10-07"));
        Assert.Equal(0, await Count("amount=45001"));
        Assert.Equal(0, await Count("booked_from=2026-10-08"));
        Assert.Equal(0, await Count("ingestion_status=disputed"));
        await client.GetAsync($"{Transactions}?ingestion_status=paid").ExpectErrorAsync(HttpStatusCode.BadRequest, "invalid_request");
        await client.GetAsync($"{Transactions}?booked_from=7.10.2026").ExpectErrorAsync(HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact] // AC 3
    public async Task Spoofed_forwarded_and_unauthenticated_notices_create_no_transaction()
    {
        var account = await NewAccountAsync();
        var notice = SyntheticBankProfile.Notice(ParsingHarness.Incoming(account.Bank));
        var cases = new (InboundDelivery Delivery, string Reason)[]
        {
            (ParsingHarness.Delivery(account, notice, from: "Banka <notify@bank.pairrot-fixture.net.evil.example>"), ParseReasons.SenderNotAllowed),
            (ParsingHarness.Delivery(account, notice, from: "Jana <jana@seznam.cz>"), ParseReasons.SenderNotAllowed),
            (ParsingHarness.Delivery(account, notice, authentication: new("pass", "pass", "fail", "smtp_receiving_server", null)), ParseReasons.DmarcNotPass),
            (ParsingHarness.Delivery(account, notice, authentication: SenderAuthentication.Unknown), ParseReasons.AuthenticationUnknown),
            // Results that are not from a receiving system, e.g. copied from a forged header, are no evidence.
            (ParsingHarness.Delivery(account, notice, authentication: new("pass", "pass", "pass", null, null)), ParseReasons.AuthenticationUnknown),
        };

        foreach (var (delivery, reason) in cases)
        {
            var messageId = await P.DeliverAsync(delivery);
            var run = Assert.Single((await RunsAsync(account, messageId))["runs"]!.AsArray())!;
            Assert.Equal(("review_required", "untrusted"), (run.Str("outcome"), run.Str("trust_decision")));
            Assert.Contains(run["reasons"]!.AsArray(), r => r!.Str("code") == reason);
            Assert.Equal(45000, run["candidate"]!["amount"]!.GetValue<long>()); // kept as a candidate for review
            Assert.Null(run["transaction_id"]);
        }

        Assert.Empty(await P.TransactionsAsync(account.ConnectionId));
        Assert.Empty((await P.Client(account.Key).GetAsync(Transactions).ExpectAsync(HttpStatusCode.OK)).Items());
    }

    [Fact] // AC 4, 5
    public async Task Other_notices_are_classified_without_creating_payments_or_retry_loops()
    {
        var account = await NewAccountAsync();
        var cases = new (string Text, string Outcome, string Reason)[]
        {
            (SyntheticBankProfile.Notice(("Type", "outgoing"), ("Amount", "450,00 CZK")), "not_payment", ParseReasons.OutgoingTransfer),
            (SyntheticBankProfile.Notice(("Type", "balance"), ("Amount", "99 450,00 CZK")), "not_payment", ParseReasons.BalanceSummary),
            (SyntheticBankProfile.Notice(("Type", "pending"), ("Amount", "450,00 CZK")), "not_payment", ParseReasons.PendingMovement),
            (SyntheticBankProfile.Notice(("Type", "correction"), ("Amount", "450,00 CZK")), "review_required", ParseReasons.CorrectionNotice),
            ("Vážený kliente, změnili jsme podmínky.", "unsupported", ParseReasons.TemplateNotRecognized),
            (SyntheticBankProfile.Notice(ParsingHarness.Incoming(account.Bank, amount: "450,005 CZK")), "review_required", ParseReasons.AmountPrecision),
        };

        foreach (var (text, outcome, reason) in cases)
        {
            var messageId = await P.DeliverAsync(ParsingHarness.Delivery(account, text));
            var history = await RunsAsync(account, messageId);
            var run = Assert.Single(history["runs"]!.AsArray())!;
            Assert.Equal((outcome, reason), (run.Str("outcome"), run.Str("reason_code")));
            Assert.Equal("succeeded", Assert.Single(history["jobs"]!.AsArray())!.Str("state"));
        }

        Assert.Empty(await P.TransactionsAsync(account.ConnectionId));
    }

    [Fact] // AC 7
    public async Task Proven_references_link_or_dispute_and_uncertain_similarity_is_flagged()
    {
        var account = await NewAccountAsync();
        var first = await P.DeliverAsync(account, ParsingHarness.Incoming(account.Bank, reference: "R-1"));
        var second = await P.DeliverAsync(account, ParsingHarness.Incoming(account.Bank, reference: "R-1"));

        var tx = Assert.Single(await P.TransactionsAsync(account.ConnectionId));
        Assert.Equal(IngestionStatus.Accepted, tx.IngestionStatus);
        var duplicate = Assert.Single((await RunsAsync(account, second))["runs"]!.AsArray())!;
        Assert.Equal(("duplicate", tx.Id.ToString()), (duplicate.Str("outcome"), duplicate.Str("transaction_id")));
        var detail = await P.Client(account.Key).GetAsync($"{Transactions}/{tx.Id}").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal([first.ToString(), second.ToString()], detail["sources"]!.AsArray().Select(s => s!.Str("inbound_message_id")));

        // The same reference with different facts disputes the movement instead of adding another.
        var conflicting = await P.DeliverAsync(account, ParsingHarness.Incoming(account.Bank, amount: "460,00 CZK", reference: "R-1"));
        var conflictRun = Assert.Single((await RunsAsync(account, conflicting))["runs"]!.AsArray())!;
        Assert.Equal(("review_required", ParseReasons.BankReferenceConflict), (conflictRun.Str("outcome"), conflictRun.Str("reason_code")));
        tx = Assert.Single(await P.TransactionsAsync(account.ConnectionId));
        Assert.Equal(IngestionStatus.Disputed, tx.IngestionStatus);
        Assert.Contains(ParseReasons.BankReferenceConflict, tx.IngestionReasons);

        // Different proven references stay separate even with the same amount, date, and symbol.
        await P.DeliverAsync(account, ParsingHarness.Incoming(account.Bank, reference: "R-2"));
        await P.DeliverAsync(account, ParsingHarness.Incoming(account.Bank, reference: "R-3"));
        var all = await P.TransactionsAsync(account.ConnectionId);
        Assert.Equal(3, all.Count);
        Assert.All(all.Where(t => t.BankTransactionId != "R-1"), t => Assert.Equal(IngestionStatus.Accepted, t.IngestionStatus));

        // Without a reference, identical-looking messages are two records flagged for review, never merged.
        var other = await NewAccountAsync();
        await P.DeliverAsync(other, ParsingHarness.Incoming(other.Bank));
        var lookalike = await P.DeliverAsync(other, ParsingHarness.Incoming(other.Bank));
        var flagged = await P.TransactionsAsync(other.ConnectionId);
        Assert.Equal(2, flagged.Count);
        Assert.All(flagged, t => Assert.Equal(IngestionStatus.ReviewRequired, t.IngestionStatus));
        var hint = Assert.Single((await RunsAsync(other, lookalike))["runs"]!.AsArray())!;
        Assert.Equal(("parsed", ParseReasons.PossibleDuplicate), (hint.Str("outcome"), hint.Str("reason_code")));
        var review = await P.Client(other.Key).GetAsync($"{Transactions}?ingestion_status=review_required").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(2, review.Items().Count);
        Assert.Empty((await P.Client(other.Key).GetAsync($"{Transactions}?ingestion_status=accepted").ExpectAsync(HttpStatusCode.OK)).Items());
    }

    [Fact] // AC 8
    public async Task Reprocessing_with_an_upgraded_parser_keeps_or_disputes_the_transaction()
    {
        var account = await NewAccountAsync();
        var client = P.Client(account.Key);
        var messageId = await P.DeliverAsync(account, ParsingHarness.Incoming(account.Bank));
        var original = Assert.Single(await P.TransactionsAsync(account.ConnectionId));

        try
        {
            // Same facts: the transaction and its ID stay.
            P.Profile.Version = "1.1.0";
            var queued = await client.PostAsync($"{Messages}/{messageId}/reprocess", null).ExpectAsync(HttpStatusCode.Accepted);
            Assert.Equal((2, true), (queued["generation"]!.GetValue<int>(), queued["created"]!.GetValue<bool>()));
            var repeated = await client.PostAsync($"{Messages}/{messageId}/reprocess", null).ExpectAsync(HttpStatusCode.Accepted);
            Assert.Equal((queued.Str("job_id"), false), (repeated.Str("job_id"), repeated["created"]!.GetValue<bool>()));
            await P.RunJobsAsync();

            var runs = (await RunsAsync(account, messageId))["runs"]!.AsArray();
            Assert.Equal(2, runs.Count);
            Assert.Equal(("1.1.0", "parsed", ParseReasons.Unchanged), (runs[1]!.Str("parser_version"), runs[1]!.Str("outcome"), runs[1]!.Str("reason_code")));
            Assert.Equal(original.Id.ToString(), runs[1]!.Str("transaction_id"));
            var kept = Assert.Single(await P.TransactionsAsync(account.ConnectionId));
            Assert.Equal((original.Id, IngestionStatus.Accepted, "1.0.0"), (kept.Id, kept.IngestionStatus, kept.ParserVersion));

            // Changed critical facts: disputed, history kept, the proposal in the new run.
            P.Profile.Version = "1.2.0";
            P.Profile.Adjust = fields => fields with { Amount = "540,00" };
            await client.PostAsync($"{Messages}/{messageId}/reprocess", null).ExpectAsync(HttpStatusCode.Accepted);
            await P.RunJobsAsync();

            runs = (await RunsAsync(account, messageId))["runs"]!.AsArray();
            Assert.Equal(3, runs.Count);
            Assert.Equal(("review_required", ParseReasons.ReprocessingChangedFacts), (runs[2]!.Str("outcome"), runs[2]!.Str("reason_code")));
            Assert.Equal(54000, runs[2]!["candidate"]!["amount"]!.GetValue<long>());
            Assert.Equal(("parsed", "parsed"), (runs[0]!.Str("outcome"), runs[0]!.Str("reason_code"))); // never rewritten
            var disputed = Assert.Single(await P.TransactionsAsync(account.ConnectionId));
            Assert.Equal((original.Id, 45000L, IngestionStatus.Disputed), (disputed.Id, disputed.Amount, disputed.IngestionStatus));

            var tx = await client.GetAsync($"{Transactions}/{original.Id}").ExpectAsync(HttpStatusCode.OK);
            var reason = Assert.Single(tx["ingestion_reasons"]!.AsArray())!;
            Assert.Equal((ParseReasons.ReprocessingChangedFacts, runs[2]!.Str("id")), (reason.Str("code"), reason.Str("parse_run_id")));

            await fx.WithDbAsync(async db => Assert.True(await db.AuditEvents.AnyAsync(e =>
                e.TargetTenantId == account.TenantId && e.Action == AuditActions.InboundMessageReprocessRequested)));
        }
        finally
        {
            P.Profile.Version = "1.0.0";
            P.Profile.Adjust = null;
        }
    }

    [Fact] // AC 13, 8: equivalent snapshots from a fake Resend account and an SMTP-style adapter
    public async Task Resend_and_smtp_style_snapshots_parse_identically_and_reprocess_without_the_provider()
    {
        var box = await fx.Inbound.CreateMailboxAsync();
        var resendAccount = await P.WithDbAsync(async db =>
            CzechBankAccount.ParseIban((await db.BankConnections.SingleAsync(b => b.Id == box.ConnectionId)).Iban).Account!);
        var emailId = InboundHarness.NewEmailId();
        var notice = SyntheticBankProfile.Notice(ParsingHarness.Incoming(resendAccount));
        fx.Inbound.Api.Respond(emailId, FakeResendApi.Ok(InboundHarness.Content(emailId, [box.Address], notice, html: null)));
        var resendEvent = InboundHarness.ReceivedEvent(emailId, box.Address).Replace("Česká spořitelna <info@csas.cz>", SyntheticBankProfile.Sender);
        await fx.Inbound.PostEventAsync(resendEvent, factory: P.Factory).ExpectAsync(HttpStatusCode.OK);
        await P.RunJobsAsync();
        var resendMessage = await P.WithDbAsync(db => db.InboundMessages.AsNoTracking().SingleAsync(m => m.SourceMessageId == emailId));

        var smtp = await NewAccountAsync();
        var smtpMessage = await P.DeliverAsync(ParsingHarness.Delivery(smtp, SyntheticBankProfile.Notice(ParsingHarness.Incoming(smtp.Bank))));

        var viaResend = Assert.Single((await P.Client(box.Key).GetAsync($"{Messages}/{resendMessage.Id}/parse-runs").ExpectAsync(HttpStatusCode.OK))["runs"]!.AsArray())!;
        var viaSmtp = Assert.Single((await RunsAsync(smtp, smtpMessage))["runs"]!.AsArray())!;
        foreach (var field in new[] { "outcome", "reason_code", "trust_decision", "template_id", "source_part", "parser_version" })
        {
            Assert.Equal(viaSmtp[field]!.ToJsonString(), viaResend[field]!.ToJsonString());
        }

        foreach (var field in new[] { "amount", "currency", "variable_symbol", "booked_on", "occurred_at", "sender_account", "destination" })
        {
            Assert.Equal(viaSmtp["candidate"]![field]!.ToJsonString(), viaResend["candidate"]![field]!.ToJsonString());
        }

        Assert.Equal("parsed", viaResend.Str("outcome"));
        Assert.Equal("resend_receiving_api", viaResend["trust"]!["evidence"]!.Str("authentication_origin"));

        // Reprocessing reads the saved snapshot; the provider is never asked again.
        var calls = fx.Inbound.Api.CallsFor(emailId);
        await P.Client(box.Key).PostAsync($"{Messages}/{resendMessage.Id}/reprocess", null).ExpectAsync(HttpStatusCode.Accepted);
        await P.RunJobsAsync();
        Assert.Equal(calls, fx.Inbound.Api.CallsFor(emailId));
        Assert.Equal(2, (await P.Client(box.Key).GetAsync($"{Messages}/{resendMessage.Id}/parse-runs").ExpectAsync(HttpStatusCode.OK))["runs"]!.AsArray().Count);

        // The adapter's name never strengthens weak evidence.
        var weak = await P.DeliverAsync(ParsingHarness.Delivery(smtp, SyntheticBankProfile.Notice(ParsingHarness.Incoming(smtp.Bank)),
            authentication: new SenderAuthentication("pass", "pass", "unknown", "smtp_receiving_server", null)));
        Assert.Equal("untrusted", Assert.Single((await RunsAsync(smtp, weak))["runs"]!.AsArray())!.Str("trust_decision"));
    }

    [Fact] // AC 10
    public async Task Fixture_records_stay_identifiable_as_test_evidence()
    {
        var account = await NewAccountAsync(InboundHarnessInline);
        await P.DeliverAsync(account, ParsingHarness.Incoming(account.Bank));

        var tx = Assert.Single((await P.Client(account.Key).GetAsync(Transactions).ExpectAsync(HttpStatusCode.OK)).Items())!;
        Assert.Equal("fixture", tx.Str("evidence"));
        var stored = Assert.Single(await P.TransactionsAsync(account.ConnectionId));
        Assert.Equal((InboundSourceKind.Fixture, InboundEnvironment.Test), (stored.SourceKind, stored.Environment));
    }

    private const string InboundHarnessInline = InboundHarness.InlineInstanceId;

    [Fact] // AC 6
    public async Task Concurrent_workers_and_restarts_keep_one_transaction_per_identity()
    {
        var account = await NewAccountAsync();
        var a = await P.AcceptAsync(ParsingHarness.Delivery(account, SyntheticBankProfile.Notice(ParsingHarness.Incoming(account.Bank, reference: "R-9"))));
        var b = await P.AcceptAsync(ParsingHarness.Delivery(account, SyntheticBankProfile.Notice(ParsingHarness.Incoming(account.Bank, reference: "R-9"))));

        // Two workers parse both messages at once.
        await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            using var scope = P.Factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<BackgroundJobRunner>().RunDueAsync();
        }));

        var tx = Assert.Single(await P.TransactionsAsync(account.ConnectionId));
        await P.WithDbAsync(async db =>
        {
            Assert.Equal(2, await db.TransactionSources.CountAsync(s => s.TransactionId == tx.Id));
            var outcomes = await db.MessageParseRuns.Where(r => r.InboundMessageId == a.MessageId || r.InboundMessageId == b.MessageId)
                .Select(r => r.Outcome).ToListAsync();
            Assert.Equal([ParseOutcome.Parsed, ParseOutcome.Duplicate], outcomes.Order());
            return 0;
        });

        // A worker stops after claiming; the job is resumed after its lease and the stale worker saves nothing.
        var c = await P.AcceptAsync(ParsingHarness.Delivery(account, SyntheticBankProfile.Notice(ParsingHarness.Incoming(account.Bank, reference: "R-10"))));
        JobLease stale;
        using (var scope = P.Factory.Services.CreateScope())
        {
            stale = (await scope.ServiceProvider.GetRequiredService<BackgroundJobRunner>().ClaimAsync())!;
            Assert.Equal((BackgroundJobTypes.ParseInboundMessage, c.MessageId!.Value), (stale.Type, stale.TargetId));
        }

        fx.Inbound.Clock.Advance(TimeSpan.FromMinutes(6));
        await P.RunJobsAsync();
        using (var scope = P.Factory.Services.CreateScope())
        {
            var handler = scope.ServiceProvider.GetServices<IBackgroundJobHandler>().Single(h => h.Type == BackgroundJobTypes.ParseInboundMessage);
            await handler.HandleAsync(stale, CancellationToken.None);
        }

        await P.WithDbAsync(async db =>
        {
            var run = await db.MessageParseRuns.SingleAsync(r => r.InboundMessageId == c.MessageId);
            Assert.Equal((1, ParseOutcome.Parsed), (run.JobGeneration, run.Outcome));
            var job = await db.BackgroundJobs.SingleAsync(j => j.Type == BackgroundJobTypes.ParseInboundMessage && j.TargetId == c.MessageId);
            Assert.Equal((BackgroundJobState.Succeeded, 2), (job.State, job.AttemptCount));
            return 0;
        });
        Assert.Equal(2, (await P.TransactionsAsync(account.ConnectionId)).Count);
    }

    [Fact] // AC 11
    public async Task Messages_stored_before_parsing_existed_are_swept_and_holds_are_respected()
    {
        var account = await NewAccountAsync();
        var old = await P.AcceptAsync(ParsingHarness.Delivery(account, SyntheticBankProfile.Notice(ParsingHarness.Incoming(account.Bank, reference: "OLD"))));
        var held = await P.AcceptAsync(ParsingHarness.Delivery(account, SyntheticBankProfile.Notice(ParsingHarness.Incoming(account.Bank, reference: "HELD"))));
        await fx.WithDbAsync(async db =>
        {
            // As if both were stored before Feature 007: no parse job exists.
            await db.BackgroundJobs.Where(j => j.Type == BackgroundJobTypes.ParseInboundMessage
                && (j.TargetId == old.MessageId || j.TargetId == held.MessageId)).ExecuteDeleteAsync();
            await db.InboundMessages.Where(m => m.Id == held.MessageId)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.HoldReason, InboundHoldReason.AddressRetired));
        });

        var queued = await P.WithServicesAsync(services => services.GetRequiredService<ParseScheduling>().QueueUnparsedAsync());
        Assert.True(queued >= 1);
        await P.RunJobsAsync();

        var tx = Assert.Single(await P.TransactionsAsync(account.ConnectionId));
        Assert.Equal("OLD", tx.BankTransactionId);
        await P.WithDbAsync(async db =>
        {
            Assert.False(await db.BackgroundJobs.AnyAsync(j => j.Type == BackgroundJobTypes.ParseInboundMessage && j.TargetId == held.MessageId));
            Assert.Null((await db.InboundMessages.SingleAsync(m => m.Id == held.MessageId)).LatestParseRunId);
            return 0;
        });

        await P.Client(account.Key).PostAsync($"{Messages}/{held.MessageId}/reprocess", null).ExpectErrorAsync(HttpStatusCode.Conflict, "message_held");
    }

    [Fact] // AC 9
    public async Task Transactions_and_parse_history_follow_tenant_and_parent_permissions()
    {
        var account = await NewAccountAsync();
        var messageId = await P.DeliverAsync(account, ParsingHarness.Incoming(account.Bank));
        var txId = Assert.Single(await P.TransactionsAsync(account.ConnectionId)).Id;
        var stranger = await fx.CreateChildAsync();
        var strangerClient = P.Client(stranger.Key);

        await strangerClient.GetAsync($"{Transactions}/{txId}").ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");
        await strangerClient.GetAsync($"{Messages}/{messageId}/parse-runs").ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");
        await strangerClient.PostAsync($"{Messages}/{messageId}/reprocess", null).ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");
        Assert.Empty((await strangerClient.GetAsync(Transactions).ExpectAsync(HttpStatusCode.OK)).Items());

        // The parent manages its direct children; another child's ID does not reach this one's records.
        var parent = P.Client(fx.RootManagementKey);
        var child = $"/v1/subtenants/{account.TenantId}";
        Assert.Single((await parent.GetAsync($"{child}/transactions").ExpectAsync(HttpStatusCode.OK)).Items());
        await parent.GetAsync($"{child}/transactions/{txId}").ExpectAsync(HttpStatusCode.OK);
        await parent.GetAsync($"{child}/inbound-messages/{messageId}/parse-runs").ExpectAsync(HttpStatusCode.OK);
        await parent.GetAsync($"/v1/subtenants/{stranger.Id}/transactions/{txId}").ExpectErrorAsync(HttpStatusCode.NotFound, "not_found");
        await parent.PostAsync($"{child}/inbound-messages/{messageId}/reprocess", null).ExpectAsync(HttpStatusCode.Accepted);
        await P.RunJobsAsync();

        // Inactive hierarchies stay readable, but parsing is held.
        await fx.SetStatusAsync(account.TenantId, TenantStatus.Suspended);
        try
        {
            await parent.GetAsync($"{child}/transactions/{txId}").ExpectAsync(HttpStatusCode.OK);
            await parent.PostAsync($"{child}/inbound-messages/{messageId}/reprocess", null).ExpectErrorAsync(HttpStatusCode.Conflict, "tenant_not_active");
        }
        finally
        {
            await fx.SetStatusAsync(account.TenantId, TenantStatus.Active);
        }

        // Content that was never stored cannot be parsed.
        var tooLarge = await P.AcceptAsync(ParsingHarness.Delivery(account, new string('x', 5 * 1024 * 1024 + 1)));
        await P.Client(account.Key).PostAsync($"{Messages}/{tooLarge.MessageId}/reprocess", null)
            .ExpectErrorAsync(HttpStatusCode.Conflict, "content_not_available");
    }
}
