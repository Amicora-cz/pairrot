using System.Globalization;
using System.Text;
using Pairrot.Core.Banking;

namespace Pairrot.Core.Payments;

public static class PaymentErrors
{
    public const string InvalidAmount = "invalid_amount";
    public const string InvalidVariableSymbol = "invalid_variable_symbol";
    public const string InvalidMessage = "invalid_message";
    public const string MessageTooLong = "message_too_long";
    public const string UnsupportedCurrency = BankAccountErrors.UnsupportedCurrency;
}

/// <summary>A rejected payment input; <see cref="Field"/> names the request field.</summary>
public sealed record PaymentError(string Code, string Field, string Message);

public sealed record SpaydParseResult(SpaydPayment? Payment, PaymentError? Error);

/// <summary>
/// A validated Czech QR Platba (SPAYD 1.0) payment and its payload. The receiving account comes
/// from a saved bank connection; everything else is validated here. Pairrot uses a strict subset
/// of the format: ACC, AM, CC, X-VS and MSG, always in that order, so identical inputs give an
/// identical payload. See https://qr-platba.cz/pro-vyvojare/specifikace-formatu/.
/// </summary>
public sealed record SpaydPayment(string Iban, long Amount, string Currency, string? VariableSymbol, string? Message)
{
    public const string FormatName = "SPAYD";

    /// <summary>Amounts are in minor units (haléře); the format allows at most 9,999,999.99.</summary>
    public const long MinAmount = 1;
    public const long MaxAmount = 999_999_999;

    public const int MaxVariableSymbolLength = 10;

    /// <summary>The format's MSG limit in characters. Longer messages are rejected, never truncated.</summary>
    public const int MaxMessageLength = 60;

    /// <summary>The complete payload, e.g. <c>SPD*1.0*ACC:CZ65...*AM:450.00*CC:CZK*X-VS:0000123456*MSG:Yoga</c>.</summary>
    public string Payload
    {
        get
        {
            var payload = new StringBuilder("SPD*1.0*")
                .Append("ACC:").Append(Iban)
                .Append("*AM:").Append(FormatAmount(Amount))
                .Append("*CC:").Append(Currency);
            if (VariableSymbol is not null)
            {
                payload.Append("*X-VS:").Append(VariableSymbol);
            }

            if (Message is not null)
            {
                payload.Append("*MSG:").Append(Message);
            }

            return payload.ToString();
        }
    }

    /// <summary>
    /// Validates payment inputs for the saved account <paramref name="iban"/>. A null amount or currency means the field was
    /// not supplied; null or omitted optional fields are absent. The message is trimmed, never shortened.
    /// </summary>
    public static SpaydParseResult Create(
        string iban, long? amount, string? currency, string? variableSymbol, string? message)
    {
        if (amount is not { } minor || minor < MinAmount || minor > MaxAmount)
        {
            return Fail(PaymentErrors.InvalidAmount, "amount",
                $"amount is required: a whole number of minor units from {MinAmount} to {MaxAmount} (45000 means CZK 450.00).");
        }

        if (currency is null)
        {
            return Fail(PaymentErrors.UnsupportedCurrency, "currency", $"currency is required and must be {CzechBankAccount.Currency}.");
        }

        if (currency != CzechBankAccount.Currency)
        {
            return Fail(PaymentErrors.UnsupportedCurrency, "currency",
                $"Only {CzechBankAccount.Currency}, the currency of the saved account, is supported.");
        }

        if (variableSymbol is not null
            && (variableSymbol.Length is 0 or > MaxVariableSymbolLength || !variableSymbol.All(char.IsAsciiDigit)))
        {
            return Fail(PaymentErrors.InvalidVariableSymbol, "variable_symbol",
                $"variable_symbol must be 1 to {MaxVariableSymbolLength} digits.");
        }

        var trimmed = message?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            trimmed = null;
        }
        else if (trimmed.IndexOfAny(ReservedCharacters) >= 0)
        {
            return Fail(PaymentErrors.InvalidMessage, "message",
                "message cannot contain '*' or '%': '*' separates payment fields and banking apps do not accept '%'.");
        }
        else if (!trimmed.EnumerateRunes().All(IsAllowed))
        {
            return Fail(PaymentErrors.InvalidMessage, "message", "message cannot contain control characters or invalid Unicode text.");
        }
        else if (trimmed.EnumerateRunes().Count() is var length && length > MaxMessageLength)
        {
            return Fail(PaymentErrors.MessageTooLong, "message",
                $"message has {length} characters; at most {MaxMessageLength} fit into the payment.");
        }

        return new SpaydParseResult(new SpaydPayment(iban, minor, CzechBankAccount.Currency, variableSymbol, trimmed), null);
    }

    /// <summary>Minor units as a decimal amount with two digits after the point, without floating point.</summary>
    public static string FormatAmount(long minor) =>
        string.Create(CultureInfo.InvariantCulture, $"{minor / 100}.{minor % 100:D2}");

    /// <summary>
    /// Characters a message cannot contain. QR Platba would percent-encode them, but banking apps
    /// tested so far (George) show the escapes literally and refuse to send the payment; other
    /// text, including Czech letters, goes into the payload unchanged as UTF-8.
    /// </summary>
    private static readonly char[] ReservedCharacters = ['*', '%'];

    private static bool IsAllowed(Rune rune) =>
        rune != Rune.ReplacementChar && !Rune.IsControl(rune) && Rune.GetUnicodeCategory(rune) is not
            (UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
            or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned);

    private static SpaydParseResult Fail(string code, string field, string message) => new(null, new PaymentError(code, field, message));
}
