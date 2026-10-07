using Pairrot.Core.Banking;
using Pairrot.Core.Inbound;
using Pairrot.Core.Parsing;

namespace Pairrot.UnitTests;

/// <summary>
/// The provisional Česká spořitelna profile. These messages are RECONSTRUCTED from public
/// descriptions of the George "Přišla platba" e-mail, not real samples: they check that the
/// profile reads only labeled values and sends every doubt to review. Replace them with
/// sanitized real fixtures once a direct-delivery sample exists.
/// </summary>
public class CeskaSporitelnaProfileTests
{
    private static readonly CzechBankAccount Account = CzechBankAccount.ParseDomestic("19-2000145399/0800").Account!;
    private static readonly CeskaSporitelnaProfile Profile = new();
    private static readonly SenderAuthentication Passing = new("pass", "pass", "pass", "resend_receiving_api", null);
    private const string Bank = "Česká spořitelna <ceskasporitelna@csas.cz>";

    private static string Reconstructed(
        string account = "19-2000145399/0800", string amount = "1 250,00 Kč", string? vs = "2026000123", string? booked = "7. 10. 2026") =>
        $"""
        Vážený pane Nováku,
        na účet {account} právě dorazila platba ve výši {amount}.
        Na účtu je nově k dispozici 25 430,12 Kč (včetně kontokorentu 5 000 Kč).
        Vaše Česká spořitelna

        Informace o transakci
        Z účtu: 19-2000145399/0100
        Název protiúčtu: Jana Nováková
        {(vs is null ? "" : $"Variabilní symbol: {vs}")}
        Zpráva pro příjemce: Lekce jógy
        {(booked is null ? "" : $"Datum zaúčtování: {booked}")}
        """;

    private static ParseEvaluation Evaluate(string? text, string? from = Bank, SenderAuthentication? auth = null, string? html = null) =>
        BankMessageEvaluator.Evaluate(Profile, new BankMessage(
            from, "Přišla platba", text, html, auth ?? Passing, InboundSourceKind.Received, InboundEnvironment.Live,
            DateTimeOffset.Parse("2026-10-07T07:16:00Z"), Account, "CZK"));

    [Fact]
    public void Incoming_payment_reads_amount_and_account_from_the_opening_sentence_not_the_balance()
    {
        var result = Evaluate(Reconstructed());

        Assert.Equal(ParseOutcome.Parsed, result.Outcome);
        Assert.Equal(("george_incoming_provisional", TrustDecision.Trusted), (result.TemplateId, result.Trust!.Decision));
        var c = result.Candidate!;
        Assert.Equal((125000L, "CZK", "2026000123"), (c.Amount, c.Currency, c.VariableSymbol));
        Assert.Equal((DestinationEvidence.FullMatch, new DateOnly(2026, 10, 7)), (c.Destination, c.BookedOn));
        Assert.Equal(("19-2000145399/0100", "Jana Nováková", "Lekce jógy"), (c.SenderAccount, c.SenderName, c.Message));
        Assert.Null(c.BankReference);
        Assert.Null(c.OccurredAt);
    }

    [Fact]
    public void Without_a_symbol_the_payment_still_parses()
    {
        var result = Evaluate(Reconstructed(vs: null));

        Assert.Equal(ParseOutcome.Parsed, result.Outcome);
        Assert.Null(result.Candidate!.VariableSymbol);
    }

    [Theory]
    [InlineData("******5399/0800", null, ParseReasons.DestinationInsufficient)]
    [InlineData("2000145399/0800", null, ParseReasons.DestinationMismatch)]
    [InlineData("19-2000145399/0800", "1 250,005 Kč", ParseReasons.AmountPrecision)]
    [InlineData("19-2000145399/0800", "1 250,00 EUR", ParseReasons.CurrencyMismatch)]
    public void Doubtful_values_require_review(string account, string? amount, string reason)
    {
        var result = Evaluate(Reconstructed(account: account, amount: amount ?? "1 250,00 Kč"));

        Assert.Equal(ParseOutcome.ReviewRequired, result.Outcome);
        Assert.Contains(result.Reasons, r => r.Code == reason);
    }

    [Fact]
    public void A_missing_booking_date_is_never_taken_from_the_reception_time()
    {
        var result = Evaluate(Reconstructed(booked: null));

        Assert.Equal(ParseOutcome.ReviewRequired, result.Outcome);
        Assert.Contains(result.Reasons, r => r.Code == ParseReasons.BookedOnMissing);
    }

    [Theory]
    [InlineData("Jana <jana.novakova@gmail.com>", "pass", ParseReasons.SenderNotAllowed)] // forwarded copy
    [InlineData("Česká spořitelna <ceskasporitelna@csas.cz.evil.example>", "pass", ParseReasons.SenderNotAllowed)]
    [InlineData(Bank, "fail", ParseReasons.DmarcNotPass)]
    public void Only_direct_bank_mail_with_passing_dmarc_is_trusted(string from, string dmarc, string reason)
    {
        var result = Evaluate(Reconstructed(), from, new SenderAuthentication("pass", "pass", dmarc, "resend_receiving_api", null));

        Assert.Equal(ParseOutcome.ReviewRequired, result.Outcome);
        Assert.Contains(result.Reasons, r => r.Code == reason);
    }

    [Fact]
    public void Html_tables_give_the_same_facts_as_text()
    {
        const string html = """
            <html><body><p>Vážený pane Nováku,<br>na účet 19-2000145399/0800 právě dorazila platba ve výši 1&nbsp;250,00&nbsp;Kč.</p>
            <p>Na účtu je nově k dispozici 25&nbsp;430,12 Kč.</p><h2>Informace o transakci</h2>
            <table><tr><td>Z účtu</td><td>19-2000145399/0100</td></tr><tr><td>Variabilní symbol</td><td>2026000123</td></tr>
            <tr><td>Datum zaúčtování</td><td>7.&nbsp;10.&nbsp;2026</td></tr></table></body></html>
            """;

        Assert.Equal(ParseOutcome.Parsed, Evaluate(null, html: html).Outcome);
        Assert.Equal(ParseOutcome.Parsed, Evaluate(Reconstructed(), html: html).Outcome);
    }

    [Fact]
    public void Other_messages_are_not_incoming_payments()
    {
        Assert.Equal(ParseOutcome.Unsupported, Evaluate("Vážený kliente, změnili jsme obchodní podmínky.").Outcome);
        var outgoing = Evaluate("Z účtu 19-2000145399/0800 právě odešla platba ve výši 500,00 Kč.");
        Assert.Equal((ParseOutcome.NotPayment, ParseReasons.OutgoingTransfer), (outgoing.Outcome, outgoing.PrimaryReason.Code));
    }
}
