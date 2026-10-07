using Pairrot.Core.Payments;

namespace Pairrot.UnitTests;

public class PaymentRequestRulesTests
{
    private static readonly Guid Connection = Guid.Parse("0199b5c0-0000-7000-8000-000000000001");

    [Theory]
    [InlineData("123", "123")]
    [InlineData("000123", "123")]
    [InlineData("0000000123", "123")]
    [InlineData("1230", "1230")]
    [InlineData("0", "0")]
    [InlineData("0000000000", "0")]
    [InlineData("9999999999", "9999999999")]
    public void Variable_symbol_keys_ignore_leading_zeros(string symbol, string key) =>
        Assert.Equal(key, PaymentRequestRules.VariableSymbolKey(symbol));

    [Fact]
    public void Omitted_and_null_optional_fields_give_the_same_fingerprint()
    {
        var omitted = Draft(Input());
        var explicitNull = Draft(Input() with { Message = null, Description = null, DueAt = null });
        var blank = Draft(Input() with { Message = "   ", Description = " " });

        Assert.Equal(omitted.Fingerprint, explicitNull.Fingerprint);
        Assert.Equal(omitted.Fingerprint, blank.Fingerprint);
        Assert.Matches("^[0-9a-f]{64}$", omitted.Fingerprint);
    }

    [Fact]
    public void Equivalent_due_times_in_different_offsets_give_the_same_fingerprint()
    {
        var prague = Draft(Input() with { DueAt = DateTimeOffset.Parse("2026-10-15T18:00:00+02:00") });
        var utc = Draft(Input() with { DueAt = DateTimeOffset.Parse("2026-10-15T16:00:00Z") });

        Assert.Equal(prague.Fingerprint, utc.Fingerprint);
        Assert.Equal(TimeSpan.Zero, prague.DueAt!.Value.Offset);
    }

    [Fact]
    public void Deliberate_changes_to_payment_fields_change_the_fingerprint()
    {
        var original = Draft(Input()).Fingerprint;
        var changed = new[]
        {
            Input() with { VariableSymbol = "000123" },
            Input() with { Amount = 45001 },
            Input() with { Message = "Yoga" },
            Input() with { BankConnectionId = Guid.NewGuid() },
            Input() with { Description = "Reservation" },
            Input() with { DueAt = DateTimeOffset.Parse("2026-10-15T18:00:00Z") },
            Input() with { ExternalId = "booking_124" },
        };

        foreach (var input in changed)
        {
            Assert.NotEqual(original, Draft(input).Fingerprint);
        }
    }

    [Fact]
    public void Text_is_trimmed_before_it_is_fingerprinted() =>
        Assert.Equal(
            Draft(Input() with { ExternalId = "booking_123", Message = "Yoga" }).Fingerprint,
            Draft(Input() with { ExternalId = " booking_123 ", Message = " Yoga " }).Fingerprint);

    [Theory]
    [InlineData(null, "variable_symbol", PaymentErrors.InvalidVariableSymbol)]
    [InlineData("", "variable_symbol", PaymentErrors.InvalidVariableSymbol)]
    [InlineData("12345678901", "variable_symbol", PaymentErrors.InvalidVariableSymbol)]
    [InlineData("12a", "variable_symbol", PaymentErrors.InvalidVariableSymbol)]
    public void A_variable_symbol_is_required(string? symbol, string field, string code) =>
        AssertError(Input() with { VariableSymbol = symbol }, field, code);

    [Fact]
    public void Required_and_limited_fields_are_validated()
    {
        AssertError(Input() with { BankConnectionId = null }, "bank_connection_id", PaymentRequestErrors.InvalidBankConnection);
        AssertError(Input() with { ExternalId = null }, "external_id", PaymentRequestErrors.InvalidExternalId);
        AssertError(Input() with { ExternalId = "  " }, "external_id", PaymentRequestErrors.InvalidExternalId);
        AssertError(Input() with { ExternalId = new string('x', 201) }, "external_id", PaymentRequestErrors.InvalidExternalId);
        AssertError(Input() with { Amount = 0 }, "amount", PaymentErrors.InvalidAmount);
        AssertError(Input() with { Currency = "EUR" }, "currency", PaymentErrors.UnsupportedCurrency);
        AssertError(Input() with { Message = "100%" }, "message", PaymentErrors.InvalidMessage);
        AssertError(Input() with { Description = new string('x', 1001) }, "description", PaymentRequestErrors.InvalidDescription);

        Assert.Null(PaymentRequestRules.Validate(Input() with { ExternalId = new string('x', 200), Description = new string('x', 1000) }).Error);
    }

    [Theory]
    [InlineData("2026-10-15T18:00:00+02:00", true)]
    [InlineData("2026-10-15T16:00:00Z", true)]
    [InlineData("2026-10-15T16:00Z", true)]
    [InlineData("2026-10-15T16:00:00.123456-05:00", true)]
    [InlineData("2026-10-15T18:00:00", false)]
    [InlineData("2026-10-15", false)]
    [InlineData("15.10.2026 18:00 +02:00", false)]
    [InlineData("2026-13-15T18:00:00Z", false)]
    [InlineData("", false)]
    public void Due_times_need_an_iso_timestamp_with_an_offset(string text, bool valid) =>
        Assert.Equal(valid, PaymentRequestRules.TryParseDueAt(text, out _));

    [Fact]
    public void Only_open_requests_past_their_due_time_are_overdue()
    {
        var now = DateTimeOffset.Parse("2026-10-15T12:00:00Z");
        var request = new PaymentRequest
        {
            ExternalId = "x", AccountNumber = "x", Iban = "x", Currency = "CZK", VariableSymbol = "1", VariableSymbolKey = "1",
            CreationFingerprint = "x", QrPayload = "x", QrFormat = "SPAYD", QrGeneratorVersion = "x",
        };

        Assert.False(PaymentRequestRules.IsOverdue(request, now));
        request.DueAt = now.AddMinutes(1);
        Assert.False(PaymentRequestRules.IsOverdue(request, now));
        request.DueAt = now.AddMinutes(-1);
        Assert.True(PaymentRequestRules.IsOverdue(request, now));
        request.Status = PaymentRequestStatus.Cancelled;
        Assert.False(PaymentRequestRules.IsOverdue(request, now));
    }

    private static PaymentRequestInput Input() =>
        new(Connection, "booking_123", 45000, "CZK", "2026000123", null, null, null);

    private static PaymentRequestDraft Draft(PaymentRequestInput input)
    {
        var result = PaymentRequestRules.Validate(input);
        Assert.Null(result.Error);
        return result.Draft!;
    }

    private static void AssertError(PaymentRequestInput input, string field, string code)
    {
        var result = PaymentRequestRules.Validate(input);
        Assert.Null(result.Draft);
        Assert.Equal(field, result.Error!.Field);
        Assert.Equal(code, result.Error.Code);
    }
}
