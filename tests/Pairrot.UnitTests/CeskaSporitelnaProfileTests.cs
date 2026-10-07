using Pairrot.Core.Banking;
using Pairrot.Core.Inbound;
using Pairrot.Core.Parsing;

namespace Pairrot.UnitTests;

/// <summary>
/// The Česká spořitelna profile against the sanitized real notification
/// (<c>tests/Fixtures/ceska-sporitelna/incoming-transfer-with-vs.html</c>, received 2026-10-07).
/// Variants are derived from that fixture by editing single values, so their structure is the real one.
/// </summary>
public class CeskaSporitelnaProfileTests
{
    private static readonly CzechBankAccount Account = CzechBankAccount.ParseDomestic("19-2000145399/0800").Account!;
    private static readonly CeskaSporitelnaProfile Profile = new();
    private static readonly SenderAuthentication Passing = new("pass", "pass", "pass", "resend_receiving_api", null);
    private const string Bank = "ceskasporitelna@csas.cz";

    private static readonly string Sample = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ceska-sporitelna", "incoming-transfer-with-vs.html"));

    private static ParseEvaluation Evaluate(string? html, string? from = Bank, SenderAuthentication? auth = null, string? text = null) =>
        BankMessageEvaluator.Evaluate(Profile, new BankMessage(
            from, "Přišla platba", text, html, auth ?? Passing, InboundSourceKind.Received, InboundEnvironment.Live,
            DateTimeOffset.Parse("2026-10-07T12:34:29Z"), Account, "CZK"));

    [Fact] // Feature 007 criterion 1, against the real sample
    public void The_real_incoming_notification_yields_its_fields()
    {
        var result = Evaluate(Sample);

        Assert.Equal(ParseOutcome.Parsed, result.Outcome);
        Assert.Equal(("george_incoming_v1", SourcePart.Html, TrustDecision.Trusted), (result.TemplateId, result.Part, result.Trust!.Decision));
        var c = result.Candidate!;
        Assert.Equal((2000L, "CZK", "2026000123"), (c.Amount, c.Currency, c.VariableSymbol));
        Assert.Equal((DestinationEvidence.FullMatch, new DateOnly(2026, 10, 7)), (c.Destination, c.BookedOn));
        Assert.Equal(DateTimeOffset.Parse("2026-10-07T14:29:00+02:00"), c.OccurredAt);
        Assert.Equal(("2000145399/0300", "Testovaci platba CSAS"), (c.SenderAccount, c.Message));
        Assert.Null(c.SenderName);
        Assert.Null(c.BankReference);
    }

    [Fact]
    public void The_balance_is_never_taken_as_the_amount()
    {
        var result = Evaluate(Sample.Replace("1&nbsp;000,00", "20&nbsp;000,00"));

        Assert.Equal(2000L, result.Candidate!.Amount);
    }

    [Fact]
    public void Without_a_symbol_the_payment_still_parses()
    {
        var withoutSymbol = System.Text.RegularExpressions.Regex.Replace(Sample, @"Variabilní symbol:", "Poznámka:");
        var result = Evaluate(withoutSymbol);

        Assert.Equal(ParseOutcome.Parsed, result.Outcome);
        Assert.Null(result.Candidate!.VariableSymbol);
    }

    [Fact]
    public void An_amount_that_disagrees_with_the_opening_sentence_is_not_guessed()
    {
        var result = Evaluate(Sample.Replace(">20,00 Kč.<", ">200,00 Kč.<"));

        Assert.Equal(ParseOutcome.Unsupported, result.Outcome);
    }

    [Fact]
    public void An_outgoing_direction_is_not_a_payment()
    {
        var result = Evaluate(Sample.Replace("příchozí", "odchozí"));

        Assert.Equal((ParseOutcome.NotPayment, ParseReasons.OutgoingTransfer), (result.Outcome, result.PrimaryReason.Code));
    }

    [Fact]
    public void Another_destination_account_requires_review()
    {
        var result = Evaluate(Sample.Replace("19-2000145399/0800", "2000145399/0800"));

        Assert.Equal(ParseOutcome.ReviewRequired, result.Outcome);
        Assert.Contains(result.Reasons, r => r.Code == ParseReasons.DestinationMismatch);
    }

    [Theory]
    [InlineData("Jan <jan.novak@gmail.com>", "pass", ParseReasons.SenderNotAllowed)]
    [InlineData("Česká spořitelna <ceskasporitelna@csas.cz.evil.example>", "pass", ParseReasons.SenderNotAllowed)]
    [InlineData(Bank, "fail", ParseReasons.DmarcNotPass)]
    public void Only_authenticated_bank_mail_is_trusted(string from, string dmarc, string reason)
    {
        var result = Evaluate(Sample, from, new SenderAuthentication("pass", "pass", dmarc, "resend_receiving_api", null));

        Assert.Equal(ParseOutcome.ReviewRequired, result.Outcome);
        Assert.Contains(result.Reasons, r => r.Code == reason);
    }

    [Fact]
    public void A_disagreeing_text_part_requires_review()
    {
        var text = HtmlText.ToText(Sample).Replace("Částka v měně účtu: 20,00 Kč", "Částka v měně účtu: 25,00 Kč").Replace("ve výši 20,00", "ve výši 25,00");

        Assert.Equal(ParseOutcome.Parsed, Evaluate(Sample, text: HtmlText.ToText(Sample)).Outcome);
        Assert.Contains(Evaluate(Sample, text: text).Reasons, r => r.Code == ParseReasons.TextHtmlConflict);
    }

    [Fact]
    public void Other_messages_are_unsupported()
    {
        Assert.Equal(ParseOutcome.Unsupported, Evaluate("<p>Vážený kliente, změnili jsme obchodní podmínky.</p>").Outcome);
        Assert.Equal(ParseOutcome.Unsupported, Evaluate(Sample.Replace("příchozí", "neznámý")).Outcome);
    }
}
