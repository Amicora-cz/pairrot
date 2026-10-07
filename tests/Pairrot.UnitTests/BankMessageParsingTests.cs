using Pairrot.Core.Banking;
using Pairrot.Core.Inbound;
using Pairrot.Core.Parsing;
using Pairrot.Tests.Shared;

namespace Pairrot.UnitTests;

/// <summary>
/// The common parsing rules of Feature 007, exercised through a synthetic test profile. They say
/// nothing about Česká spořitelna's real format, which is still unverified.
/// </summary>
public class BankMessageParsingTests
{
    private static readonly CzechBankAccount Account = CzechBankAccount.ParseDomestic("19-2000145399/0800").Account!;
    private static readonly SenderAuthentication Passing = new("pass", "pass", "pass", "test_receiving_server", null);
    private static readonly SyntheticBankProfile Profile = new();

    private static (string, string?)[] Incoming(
        string? amount = "1 234,50 CZK", string? vs = "000123", string? account = "19-2000145399/0800",
        string? booked = "07.10.2026", string? time = "07.10.2026 09:15", string? reference = "R-1") =>
    [
        ("Type", "incoming"), ("Account", account), ("Amount", amount), ("Booked", booked), ("Time", time), ("VS", vs),
        ("Reference", reference), ("From account", "000000-0000000123/0100"), ("From name", "Jana Nováková"), ("Message", "Lekce jógy"),
    ];

    private static BankMessage Message(
        string? text, string? html = null, string? from = SyntheticBankProfile.Sender, SenderAuthentication? auth = null,
        InboundSourceKind kind = InboundSourceKind.Received) =>
        new(from, "Notice", text, html, auth ?? Passing, kind, InboundEnvironment.Live,
            DateTimeOffset.Parse("2026-10-07T07:16:00Z"), Account, "CZK");

    private static ParseEvaluation Evaluate(BankMessage message, IBankProfile? profile = null) =>
        BankMessageEvaluator.Evaluate(profile ?? Profile, message);

    private static string[] Codes(ParseEvaluation evaluation) => evaluation.Reasons.Select(r => r.Code).ToArray();

    [Fact] // AC 1
    public void A_trusted_incoming_notice_yields_normalized_fields()
    {
        var result = Evaluate(Message(SyntheticBankProfile.Notice(Incoming())));

        Assert.Equal(ParseOutcome.Parsed, result.Outcome);
        Assert.Equal(TrustDecision.Trusted, result.Trust!.Decision);
        Assert.Equal(("synthetic_v1", SourcePart.Text, MessageKind.IncomingTransfer), (result.TemplateId, result.Part, result.Kind));
        var c = result.Candidate!;
        Assert.Equal(123450L, c.Amount);
        Assert.Equal("CZK", c.Currency);
        Assert.Equal(("000123", "123"), (c.VariableSymbol, c.VariableSymbolKey));
        Assert.Equal(DestinationEvidence.FullMatch, c.Destination);
        Assert.Equal(new DateOnly(2026, 10, 7), c.BookedOn);
        Assert.Equal(DateTimeOffset.Parse("2026-10-07T09:15:00+02:00"), c.OccurredAt);
        Assert.Equal(TimeSpan.FromHours(2), c.OccurredAt!.Value.Offset);
        Assert.Equal(("123/0100", "Jana Nováková", "Lekce jógy", "R-1"), (c.SenderAccount, c.SenderName, c.Message, c.BankReference));
        Assert.Equal("000000-0000000123/0100", c.Raw.SenderAccount);
    }

    [Fact] // AC 2
    public void A_notice_without_symbol_or_optional_fields_still_parses()
    {
        var result = Evaluate(Message(SyntheticBankProfile.Notice(
            ("Type", "incoming"), ("Account", "19-2000145399/0800"), ("Amount", "450,00 CZK"), ("Booked", "07.10.2026"))));

        Assert.Equal(ParseOutcome.Parsed, result.Outcome);
        var c = result.Candidate!;
        Assert.Equal((45000L, (string?)null, (string?)null), (c.Amount, c.VariableSymbol, c.VariableSymbolKey));
        Assert.Null(c.OccurredAt); // no invented midnight
        Assert.Null(c.BankReference);
    }

    [Theory] // AC 2, 5
    [InlineData(null, ParseReasons.AmountMissing)]
    [InlineData("12,345 CZK", ParseReasons.AmountPrecision)]
    [InlineData("99 999 999 999 999 999,00 CZK", ParseReasons.AmountOverflow)]
    [InlineData("0,00 CZK", ParseReasons.AmountInvalid)]
    [InlineData("-100,00 CZK", ParseReasons.AmountInvalid)]
    [InlineData("1 23,00 CZK", ParseReasons.AmountInvalid)]
    [InlineData("12,50", ParseReasons.CurrencyMissing)]
    [InlineData("12,50 EUR", ParseReasons.CurrencyMismatch)]
    [InlineData("12,50 XYZ", ParseReasons.CurrencyInvalid)]
    public void Amount_and_currency_problems_require_review(string? amount, string reason)
    {
        var result = Evaluate(Message(SyntheticBankProfile.Notice(Incoming(amount: amount))));

        Assert.Equal(ParseOutcome.ReviewRequired, result.Outcome);
        Assert.Contains(reason, Codes(result));
    }

    [Fact]
    public void Large_amounts_beyond_the_qr_limit_are_accepted_exactly()
    {
        var result = Evaluate(Message(SyntheticBankProfile.Notice(Incoming(amount: "12.345.678,90 Kč"))));

        Assert.Equal(ParseOutcome.Parsed, result.Outcome);
        Assert.Equal((1234567890L, "CZK"), (result.Candidate!.Amount, result.Candidate.Currency));
    }

    [Theory] // AC 5
    [InlineData("12A", ParseReasons.InvalidVariableSymbol)]
    [InlineData("12345678901", ParseReasons.InvalidVariableSymbol)]
    public void An_invalid_symbol_is_a_review_reason_not_an_empty_value(string vs, string reason)
    {
        var result = Evaluate(Message(SyntheticBankProfile.Notice(Incoming(vs: vs))));

        Assert.Equal(ParseOutcome.ReviewRequired, result.Outcome);
        Assert.Contains(reason, Codes(result));
        Assert.Null(result.Candidate!.VariableSymbol);
        Assert.Equal(vs, result.Candidate.Raw.VariableSymbol);
    }

    [Theory] // AC 5
    [InlineData(null, "07.10.2026 09:15", ParseReasons.BookedOnMissing)]
    [InlineData("2026-10-07", "07.10.2026 09:15", ParseReasons.BookedOnInvalid)]
    [InlineData("10/07/2026", "07.10.2026 09:15", ParseReasons.BookedOnInvalid)]
    [InlineData("07.10.2026", "7.10.2026 9:15", ParseReasons.OccurredAtInvalid)]
    [InlineData("25.10.2026", "25.10.2026 02:30", ParseReasons.OccurredAtAmbiguous)]
    public void Date_problems_are_visible(string? booked, string time, string reason)
    {
        var result = Evaluate(Message(SyntheticBankProfile.Notice(Incoming(booked: booked, time: time))));

        Assert.Equal(ParseOutcome.ReviewRequired, result.Outcome);
        Assert.Contains(reason, Codes(result));
    }

    [Theory] // AC 2: destination evidence
    [InlineData("19-2000145399/0800", ParseOutcome.Parsed, null)]
    [InlineData("CZ65 0800 0000 1920 0014 5399", ParseOutcome.Parsed, null)]
    [InlineData("******5399/0800", ParseOutcome.Parsed, null)]
    [InlineData("****5399/0800", ParseOutcome.ReviewRequired, ParseReasons.DestinationInsufficient)]
    [InlineData("******9999/0800", ParseOutcome.ReviewRequired, ParseReasons.DestinationInsufficient)]
    [InlineData("2000145399/0800", ParseOutcome.ReviewRequired, ParseReasons.DestinationMismatch)]
    [InlineData("12345/0800", ParseOutcome.ReviewRequired, ParseReasons.DestinationInvalid)]
    [InlineData(null, ParseOutcome.ReviewRequired, ParseReasons.DestinationMissing)]
    public void The_destination_must_agree_with_the_saved_connection(string? account, ParseOutcome outcome, string? reason)
    {
        var result = Evaluate(Message(SyntheticBankProfile.Notice(Incoming(account: account))));

        Assert.Equal(outcome, result.Outcome);
        if (reason is not null)
        {
            Assert.Contains(reason, Codes(result));
        }
    }

    [Theory] // AC 3
    [InlineData("Česká spořitelna <attacker@evil.example>", "pass", ParseReasons.SenderNotAllowed)]
    [InlineData("\"notify@bank.pairrot-fixture.net\" <me@gmail.com>", "pass", ParseReasons.SenderNotAllowed)]
    [InlineData("Jana <jana@seznam.cz>", "pass", ParseReasons.SenderNotAllowed)] // personal forward
    [InlineData("notify@bank.pairrot-fixture.net, other@evil.example", "pass", ParseReasons.SenderInvalid)]
    [InlineData(null, "pass", ParseReasons.SenderInvalid)]
    [InlineData(SyntheticBankProfile.Sender, "fail", ParseReasons.DmarcNotPass)]
    [InlineData(SyntheticBankProfile.Sender, "none", ParseReasons.DmarcNotPass)]
    [InlineData(SyntheticBankProfile.Sender, "unknown", ParseReasons.AuthenticationUnknown)]
    public void Untrusted_origin_never_parses(string? from, string dmarc, string reason)
    {
        var auth = new SenderAuthentication("pass", "pass", dmarc, "test_receiving_server", null);
        var result = Evaluate(Message(SyntheticBankProfile.Notice(Incoming()), from: from, auth: auth));

        Assert.Equal(ParseOutcome.ReviewRequired, result.Outcome);
        Assert.Equal(TrustDecision.Untrusted, result.Trust!.Decision);
        Assert.Contains(reason, Codes(result));
        Assert.Equal(123450L, result.Candidate!.Amount); // the candidate is preserved for review
    }

    [Fact] // AC 3: no receiving-system evidence, whatever the body or headers claim
    public void Missing_provider_evidence_is_untrusted()
    {
        var text = SyntheticBankProfile.Notice(Incoming()) + "Authentication-Results: mx.example; dmarc=pass\n";
        var result = Evaluate(Message(text, auth: SenderAuthentication.Unknown));

        Assert.Equal(ParseOutcome.ReviewRequired, result.Outcome);
        Assert.Contains(ParseReasons.AuthenticationUnknown, Codes(result));
    }

    [Theory] // AC 4
    [InlineData("outgoing", ParseOutcome.NotPayment, ParseReasons.OutgoingTransfer)]
    [InlineData("balance", ParseOutcome.NotPayment, ParseReasons.BalanceSummary)]
    [InlineData("pending", ParseOutcome.NotPayment, ParseReasons.PendingMovement)]
    [InlineData("correction", ParseOutcome.ReviewRequired, ParseReasons.CorrectionNotice)]
    [InlineData("multi", ParseOutcome.Unsupported, ParseReasons.MultipleMovements)]
    [InlineData("statement", ParseOutcome.Unsupported, ParseReasons.TemplateNotRecognized)]
    public void Other_notices_never_become_incoming_payments(string type, ParseOutcome outcome, string reason)
    {
        var result = Evaluate(Message(SyntheticBankProfile.Notice(("Type", type), ("Amount", "100,00 CZK"))));

        Assert.Equal((outcome, reason), (result.Outcome, result.PrimaryReason.Code));
        Assert.Null(result.Candidate);
    }

    [Fact] // AC 5: unknown template change
    public void An_unrecognized_message_is_unsupported()
    {
        var result = Evaluate(Message("Dobrý den, na účet přišla platba 450,00 CZK."));

        Assert.Equal((ParseOutcome.Unsupported, ParseReasons.TemplateNotRecognized), (result.Outcome, result.PrimaryReason.Code));
    }

    [Fact] // AC 5
    public void Text_and_html_disagreeing_on_payment_facts_require_review()
    {
        var agreeing = Evaluate(Message(SyntheticBankProfile.Notice(Incoming()), SyntheticBankProfile.NoticeHtml(Incoming())));
        Assert.Equal(ParseOutcome.Parsed, agreeing.Outcome);

        var conflicting = Evaluate(Message(SyntheticBankProfile.Notice(Incoming()), SyntheticBankProfile.NoticeHtml(Incoming(amount: "1 234,00 CZK"))));
        Assert.Equal(ParseOutcome.ReviewRequired, conflicting.Outcome);
        Assert.Contains(ParseReasons.TextHtmlConflict, Codes(conflicting));
        Assert.Equal(123450L, conflicting.Candidate!.Amount); // the preferred part's candidate, kept for review

        var kindConflict = Evaluate(Message(SyntheticBankProfile.Notice(("Type", "outgoing")), SyntheticBankProfile.NoticeHtml(Incoming())));
        Assert.Equal((ParseOutcome.ReviewRequired, ParseReasons.TextHtmlConflict), (kindConflict.Outcome, kindConflict.PrimaryReason.Code));
    }

    [Fact]
    public void Html_is_reduced_to_inert_text()
    {
        var htmlOnly = Evaluate(Message(null, SyntheticBankProfile.NoticeHtml(Incoming())));
        Assert.Equal((ParseOutcome.Parsed, SourcePart.Html), (htmlOnly.Outcome, htmlOnly.Part));

        var text = HtmlText.ToText("<p>A&nbsp;&amp;&nbsp;B</p><script>steal()</script><style>p{}</style><!-- hidden --><a href=\"https://x\">link</a><br>C");
        Assert.Equal("A & B\nlink\nC", text);
    }

    [Fact]
    public void Profiles_parse_only_the_parts_they_have_verified()
    {
        var textOnly = new SyntheticBankProfile { Parts = [SourcePart.Text] };
        var result = Evaluate(Message(null, SyntheticBankProfile.NoticeHtml(Incoming())), textOnly);

        Assert.Equal((ParseOutcome.Unsupported, ParseReasons.NoSupportedContent), (result.Outcome, result.PrimaryReason.Code));
    }

    [Fact] // §2: the selected bank has no verified samples yet
    public void Profiles_without_verified_templates_or_senders_recognize_nothing()
    {
        var empty = new SyntheticBankProfile { Parts = [] };
        var result = Evaluate(Message(SyntheticBankProfile.Notice(Incoming())), empty);

        Assert.Equal((ParseOutcome.Unsupported, ParseReasons.NoVerifiedTemplates), (result.Outcome, result.PrimaryReason.Code));
        var noSenders = SourceTrust.Assess(Message(SyntheticBankProfile.Notice(Incoming())), SenderPolicy.None);
        Assert.Contains(noSenders.Reasons, r => r.Code == ParseReasons.NoVerifiedSenders);

        var profile = new CeskaSporitelnaProfile();
        Assert.Equal(("ceska_sporitelna", "0800"), (profile.Name, profile.BankCode));
        Assert.Same(profile, new BankProfileRegistry([profile]).For(Account));
    }

    [Fact]
    public void Banks_without_a_profile_are_unsupported()
    {
        var other = CzechBankAccount.ParseDomestic("19-2000145399/0100").Account!;
        var result = BankMessageEvaluator.Evaluate(new BankProfileRegistry([new CeskaSporitelnaProfile()]).For(other),
            Message(SyntheticBankProfile.Notice(Incoming())) with { Destination = other });

        Assert.Equal((ParseOutcome.Unsupported, ParseReasons.BankNotSupported), (result.Outcome, result.PrimaryReason.Code));
        Assert.Null(result.Trust);
        Assert.Throws<InvalidOperationException>(() => new BankProfileRegistry([new CeskaSporitelnaProfile(), new SyntheticBankProfile()]));
    }

    [Fact] // AC 10, 13: evidence class and source kind do not change the decision
    public void Fixture_source_is_recorded_without_changing_the_rules()
    {
        var received = Evaluate(Message(SyntheticBankProfile.Notice(Incoming())));
        var fixture = Evaluate(Message(SyntheticBankProfile.Notice(Incoming()), kind: InboundSourceKind.Fixture));

        Assert.Equal(received.Outcome, fixture.Outcome);
        Assert.Equal(received.Candidate!.CriticalFacts, fixture.Candidate!.CriticalFacts);
        Assert.Equal(("received", "fixture"), (received.Trust!.Evidence.SourceKind, fixture.Trust!.Evidence.SourceKind));
    }

    [Fact] // AC 7: fingerprints are hints
    public void Comparison_fingerprints_ignore_references_and_sender_details()
    {
        var connection = Guid.NewGuid();
        string Fingerprint(params (string, string?)[] fields) =>
            BankMessageEvaluator.ComparisonFingerprint("p", connection, Evaluate(Message(SyntheticBankProfile.Notice(fields))).Candidate!);

        Assert.Equal(Fingerprint(Incoming(reference: "A")), Fingerprint(Incoming(reference: "B", vs: "123")));
        Assert.NotEqual(Fingerprint(Incoming()), Fingerprint(Incoming(booked: "08.10.2026")));
        Assert.NotEqual(Fingerprint(Incoming()), Fingerprint(Incoming(amount: "1 234,51 CZK")));
    }

    [Theory]
    [InlineData("1", 100L)]
    [InlineData("1,5", 150L)]
    [InlineData("1 000 000,01", 100000001L)]
    [InlineData("1 000,00", 100000L)]
    [InlineData("1.000,00", 100000L)]
    [InlineData("92 233 720 368 547 758,07", long.MaxValue)]
    public void Amounts_parse_exactly(string text, long expected)
    {
        Assert.Null(BankAmount.TryParseMinorUnits(text, ',', ".", 2, out var units));
        Assert.Equal(expected, units);
    }

    [Theory]
    [InlineData("92 233 720 368 547 758,08", AmountError.Overflow)]
    [InlineData("1,", AmountError.Invalid)]
    [InlineData("1,2,3", AmountError.Invalid)]
    [InlineData("1e3", AmountError.Invalid)]
    [InlineData("+5", AmountError.NotPositive)]
    [InlineData("1,001", AmountError.Precision)]
    [InlineData(" ", AmountError.Missing)]
    public void Amount_errors_are_never_rounded_away(string text, AmountError expected) =>
        Assert.Equal(expected, BankAmount.TryParseMinorUnits(text, ',', ".", 2, out _));
}
