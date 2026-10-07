using Pairrot.Core.Inbound;
using Pairrot.Core.Matching;
using Pairrot.Core.Payments;
using Pairrot.Core.Transactions;

namespace Pairrot.UnitTests;

/// <summary>Feature 009's exact matching rules and payment totals, without a database.</summary>
public class MatchingRulesTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Connection = Guid.NewGuid();

    private static IncomingTransaction Transaction(long amount = 45000, string? vs = "123") => new()
    {
        TenantId = Tenant,
        BankConnectionId = Connection,
        Amount = amount,
        Currency = "CZK",
        VariableSymbol = vs,
        VariableSymbolKey = vs is null ? null : PaymentRequestRules.VariableSymbolKey(vs),
        BankProfile = "p",
        ParserVersion = "1",
        ComparisonFingerprint = new string('0', 64),
        IngestionStatus = IngestionStatus.Accepted,
        SourceKind = InboundSourceKind.Received,
        Environment = InboundEnvironment.Live,
    };

    private static PaymentRequest Request(long amount = 45000, string vs = "000123", PaymentRequestStatus status = PaymentRequestStatus.Open, long paid = 0) => new()
    {
        TenantId = Tenant,
        BankConnectionId = Connection,
        ExternalId = "x",
        AccountNumber = "x",
        Iban = "x",
        Amount = amount,
        Currency = "CZK",
        VariableSymbol = vs,
        VariableSymbolKey = PaymentRequestRules.VariableSymbolKey(vs),
        CreationFingerprint = "x",
        QrPayload = "x",
        QrFormat = "SPAYD",
        QrGeneratorVersion = "x",
        Status = status,
        PaidAmount = paid,
    };

    [Fact]
    public void Only_one_open_request_with_the_same_symbol_key_and_the_full_amount_matches()
    {
        Assert.Equal(MatchResults.Matched, MatchingRules.Decide(Transaction(), [Request()]));
        Assert.Equal(MatchResults.AmountMismatch, MatchingRules.Decide(Transaction(20000), [Request()]));
        Assert.Equal(MatchResults.RequestNotFound, MatchingRules.Decide(Transaction(), []));
        Assert.Equal(MatchResults.MissingVariableSymbol, MatchingRules.Decide(Transaction(vs: null), [Request(vs: "0")]));
        Assert.Equal(MatchResults.RequestCancelled, MatchingRules.Decide(Transaction(), [Request(status: PaymentRequestStatus.Cancelled)]));
        Assert.Equal(MatchResults.RequestAlreadyPaid,
            MatchingRules.Decide(Transaction(25000), [Request(status: PaymentRequestStatus.PartiallyPaid, paid: 20000)]));
        Assert.Equal(MatchResults.MultipleCandidates, MatchingRules.Decide(Transaction(), [Request(), Request(vs: "123")]));
        Assert.Equal(MatchResults.RequestNotFound, MatchingRules.Decide(Transaction(), [Request().Also(r => r.BankConnectionId = Guid.NewGuid())]));
        Assert.Equal(MatchResults.CurrencyMismatch, MatchingRules.Decide(Transaction(), [Request().Also(r => r.Currency = "EUR")]));
    }

    [Fact]
    public void Review_manual_confirmation_test_evidence_and_blocks_stop_automatic_matching()
    {
        Assert.Null(MatchingRules.AutomaticBlock(Transaction(), false, false));
        Assert.Equal(MatchResults.ReviewRequired, MatchingRules.AutomaticBlock(Transaction(), true, false));
        Assert.Equal(MatchResults.ReviewRequired, MatchingRules.AutomaticBlock(Transaction().Also(t => t.IngestionStatus = IngestionStatus.Disputed), false, false));
        Assert.Equal(MatchResults.NotLiveEvidence, MatchingRules.AutomaticBlock(Transaction().Also(t => t.Environment = InboundEnvironment.Test), false, false));
        Assert.Equal(MatchResults.NotLiveEvidence, MatchingRules.AutomaticBlock(Transaction().Also(t => t.SourceKind = InboundSourceKind.Fixture), false, false));
        Assert.Null(MatchingRules.AutomaticBlock(Transaction().Also(t => t.SourceKind = InboundSourceKind.Fixture), false, allowTestEvidence: true));
        Assert.Equal(MatchResults.AutoMatchingBlocked, MatchingRules.AutomaticBlock(Transaction().Also(t => t.AutoMatchBlocked = true), false, false));

        // A confirmed receipt under review is allocatable by hand only.
        var confirmed = Transaction().Also(t => (t.IngestionStatus, t.ManuallyConfirmed) = (IngestionStatus.ReviewRequired, true));
        Assert.Null(MatchingRules.ManualBlock(confirmed, false, false));
        Assert.Equal(MatchResults.ManualAllocationOnly, MatchingRules.AutomaticBlock(confirmed, false, false));
        Assert.Equal(MatchResults.ReviewRequired, MatchingRules.ManualBlock(confirmed, true, false));

        Assert.Equal(MatchResults.Duplicate, MatchingRules.ManualBlock(Transaction().Also(t => t.Handling = TransactionHandling.Duplicate), false, false));
        Assert.Equal(MatchResults.AlreadyAllocated, MatchingRules.ManualBlock(Transaction().Also(t => t.Handling = TransactionHandling.Allocated), false, false));
    }

    [Theory]
    [InlineData(45000, 0, "open", 45000, 0)]
    [InlineData(45000, 20000, "partially_paid", 25000, 0)]
    [InlineData(45000, 45000, "paid", 0, 0)]
    [InlineData(45000, 50000, "overpaid", 0, 5000)]
    public void The_status_follows_from_the_paid_amount(long amount, long paid, string status, long remaining, long overpaid)
    {
        var summary = PaymentSummary.Calculate(amount, paid);

        Assert.Equal(status, System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(summary.Status.ToString()));
        Assert.Equal((paid, (long?)remaining, overpaid), (summary.PaidAmount, summary.RemainingAmount, summary.OverpaidAmount));
    }

    [Fact]
    public void A_cancelled_request_has_nothing_paid_or_remaining_and_totals_never_overflow()
    {
        Assert.Equal(new PaymentSummary(PaymentRequestStatus.Cancelled, 0, null, 0),
            PaymentSummary.Of(Request(status: PaymentRequestStatus.Cancelled)));
        Assert.Null(PaymentSummary.Add(long.MaxValue, 1));
        Assert.Null(PaymentSummary.Add(100, -101));
        Assert.Equal(150, PaymentSummary.Add(100, 50));
    }

    [Fact]
    public void Overdue_needs_something_left_to_pay()
    {
        var now = DateTimeOffset.Parse("2026-10-07T12:00:00Z");
        var past = now.AddDays(-1);
        Assert.True(PaymentRequestRules.IsOverdue(Request().Also(r => r.DueAt = past), now));
        Assert.True(PaymentRequestRules.IsOverdue(Request(status: PaymentRequestStatus.PartiallyPaid, paid: 1).Also(r => r.DueAt = past), now));
        Assert.False(PaymentRequestRules.IsOverdue(Request(status: PaymentRequestStatus.Paid, paid: 45000).Also(r => r.DueAt = past), now));
        Assert.False(PaymentRequestRules.IsOverdue(Request(status: PaymentRequestStatus.Overpaid, paid: 50000).Also(r => r.DueAt = past), now));
    }
}

internal static class TestObjects
{
    public static T Also<T>(this T value, Action<T> change)
    {
        change(value);
        return value;
    }
}
