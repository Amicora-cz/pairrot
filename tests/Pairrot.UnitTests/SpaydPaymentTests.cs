using Pairrot.Core.Payments;

namespace Pairrot.UnitTests;

public class SpaydPaymentTests
{
    // The account from the published example payload at qr-platba.cz.
    private const string Iban = "CZ9106000000000000000123";

    [Fact]
    public void Published_example_fields_are_reproduced()
    {
        // SPD*1.0*ACC:CZ9106000000000000000123*AM:450.00*CC:CZK*MSG:PLATBA ZA ZBOZI*X-VS:1234567890
        var payload = Create(45000, "1234567890", "PLATBA ZA ZBOZI").Payload;

        Assert.Equal("SPD*1.0*ACC:CZ9106000000000000000123*AM:450.00*CC:CZK*X-VS:1234567890*MSG:PLATBA ZA ZBOZI", payload);
        Assert.Equal(new Dictionary<string, string>
        {
            ["ACC"] = Iban, ["AM"] = "450.00", ["CC"] = "CZK", ["MSG"] = "PLATBA ZA ZBOZI", ["X-VS"] = "1234567890",
        }, SpaydReader.Parse("SPD*1.0*ACC:CZ9106000000000000000123*AM:450.00*CC:CZK*MSG:PLATBA ZA ZBOZI*X-VS:1234567890"));
    }

    [Theory]
    [InlineData(1, "0.01")]
    [InlineData(99, "0.99")]
    [InlineData(100, "1.00")]
    [InlineData(45000, "450.00")]
    [InlineData(4500000, "45000.00")]
    [InlineData(999_999_999, "9999999.99")]
    public void Amounts_in_minor_units_are_formatted_exactly(long minor, string expected) =>
        Assert.Equal(expected, SpaydReader.Parse(Create(minor).Payload)["AM"]);

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(1_000_000_000L)]
    [InlineData(long.MaxValue)]
    public void Amounts_outside_the_range_are_rejected(long? minor) =>
        AssertError(SpaydPayment.Create(Iban, minor, "CZK", null, null), PaymentErrors.InvalidAmount, "amount");

    [Theory]
    [InlineData(null)]
    [InlineData("EUR")]
    [InlineData("czk")]
    [InlineData("")]
    public void Only_czk_is_accepted(string? currency) =>
        AssertError(SpaydPayment.Create(Iban, 100, currency, null, null), PaymentErrors.UnsupportedCurrency, "currency");

    [Fact]
    public void Optional_fields_are_omitted_and_order_is_fixed()
    {
        Assert.Equal("SPD*1.0*ACC:CZ9106000000000000000123*AM:1.00*CC:CZK", Create(100).Payload);
        Assert.Equal(Create(100, "0000123456", "Yoga").Payload, Create(100, "0000123456", "Yoga").Payload);
    }

    [Theory]
    [InlineData("0000123456")]
    [InlineData("0")]
    [InlineData("1")]
    public void Variable_symbols_keep_leading_zeros(string symbol) =>
        Assert.Equal(symbol, SpaydReader.Parse(Create(100, symbol).Payload)["X-VS"]);

    [Theory]
    [InlineData("")]
    [InlineData("12345678901")]
    [InlineData("12a")]
    [InlineData(" 123")]
    [InlineData("-123")]
    [InlineData("١٢٣")] // non-ASCII digits
    public void Invalid_variable_symbols_are_rejected(string symbol) =>
        AssertError(SpaydPayment.Create(Iban, 100, "CZK", symbol, null), PaymentErrors.InvalidVariableSymbol, "variable_symbol");

    [Theory]
    [InlineData("Yoga October")]
    [InlineData("Žluťoučký kůň")]
    [InlineData("100% *paid*")]
    [InlineData("a*X-VS:999*AM:1.00")]
    [InlineData("%2A literal")]
    [InlineData("key:value, 'quotes' & <tags>")]
    [InlineData("emoji 🧘")]
    public void Messages_round_trip_through_an_independent_decoder(string message)
    {
        var payment = Create(45000, "0000123456", message);
        var fields = SpaydReader.Parse(payment.Payload);

        Assert.Equal(message, fields["MSG"]);
        // A delimiter in the message cannot add or replace a field.
        Assert.Equal(["ACC", "AM", "CC", "X-VS", "MSG"], fields.Keys);
        Assert.Equal("450.00", fields["AM"]);
        Assert.Equal("0000123456", fields["X-VS"]);
    }

    [Fact]
    public void Messages_are_trimmed_and_blank_ones_omitted()
    {
        Assert.Equal("Yoga", Create(100, message: "  Yoga \t").Message);
        Assert.Null(Create(100, message: "   ").Message);
        Assert.DoesNotContain("MSG", Create(100, message: "").Payload);
    }

    [Fact]
    public void Oversized_messages_are_rejected_without_truncation()
    {
        Assert.Equal(new string('A', 60), Create(100, message: new string('A', 60)).Message);
        AssertError(SpaydPayment.Create(Iban, 100, "CZK", null, new string('A', 61)), PaymentErrors.MessageTooLong, "message");

        // Each 'ř' encodes as %C5%99, so 10 of them fill 60 characters and 11 do not.
        Assert.NotNull(Create(100, message: new string('ř', 10)).Message);
        AssertError(SpaydPayment.Create(Iban, 100, "CZK", null, new string('ř', 11)), PaymentErrors.MessageTooLong, "message");
    }

    [Theory]
    [InlineData("line\nbreak")]
    [InlineData("tab\tinside")]
    [InlineData("bell\u0007")]
    [InlineData("zero​width")]
    [InlineData("lone \uD800 surrogate")]
    public void Messages_with_control_or_invalid_characters_are_rejected(string message) =>
        AssertError(SpaydPayment.Create(Iban, 100, "CZK", null, message), PaymentErrors.InvalidMessage, "message");

    private static SpaydPayment Create(long minor, string? symbol = null, string? message = null)
    {
        var result = SpaydPayment.Create(Iban, minor, "CZK", symbol, message);
        Assert.Null(result.Error);
        return result.Payment!;
    }

    private static void AssertError(SpaydParseResult result, string code, string field)
    {
        Assert.Null(result.Payment);
        Assert.Equal(code, result.Error?.Code);
        Assert.Equal(field, result.Error!.Field);
    }
}

/// <summary>
/// A minimal SPAYD reader written from the format description, independent of the encoder:
/// split on <c>*</c>, take each key up to the first <c>:</c>, then percent-decode the value.
/// </summary>
internal static class SpaydReader
{
    public static Dictionary<string, string> Parse(string payload)
    {
        var parts = payload.Split('*');
        Assert.Equal("SPD", parts[0]);
        Assert.Equal("1.0", parts[1]);

        var fields = new Dictionary<string, string>();
        foreach (var part in parts[2..])
        {
            var colon = part.IndexOf(':');
            Assert.True(colon > 0, $"Malformed field '{part}'.");
            fields.Add(part[..colon], Uri.UnescapeDataString(part[(colon + 1)..]));
        }

        return fields;
    }
}
