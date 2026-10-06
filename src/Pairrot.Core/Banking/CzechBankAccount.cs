using System.Globalization;
using System.Text;

namespace Pairrot.Core.Banking;

/// <summary>
/// A Czech bank account in both representations. <see cref="Iban"/> is the normalized identity:
/// equivalent domestic and IBAN inputs (with or without spaces or leading zeros) produce the same value.
/// A valid format and check digits do not prove that the account exists or who owns it.
/// </summary>
public sealed record CzechBankAccount(string Prefix, string Number, string BankCode)
{
    public const string CountryCode = "CZ";
    public const string Currency = "CZK";
    public const int IbanLength = 24;

    // Weights from CNB decree 169/2011 Sb.: 2^i mod 11, read from the rightmost digit.
    private static readonly int[] PrefixWeights = [10, 5, 8, 4, 2, 1];
    private static readonly int[] NumberWeights = [6, 3, 7, 9, 10, 5, 8, 4, 2, 1];

    /// <summary>Domestic form without leading zeros, e.g. <c>19-2000145399/0800</c>.</summary>
    public string AccountNumber
    {
        get
        {
            var number = Number.TrimStart('0');
            var prefix = Prefix.TrimStart('0');
            return prefix.Length == 0 ? $"{number}/{BankCode}" : $"{prefix}-{number}/{BankCode}";
        }
    }

    /// <summary>Electronic IBAN form without spaces, e.g. <c>CZ6508000000192000145399</c>.</summary>
    public string Iban
    {
        get
        {
            var bban = BankCode + Prefix + Number;
            var check = 98 - Mod97(bban + CountryCode + "00");
            return $"{CountryCode}{check:00}{bban}";
        }
    }

    /// <summary>
    /// Parses a domestic account number, a Czech IBAN, or both. When both are supplied they must
    /// identify the same account. Returns a validation failure naming the offending field.
    /// </summary>
    public static BankAccountParseResult Parse(string? accountNumber, string? iban)
    {
        accountNumber = string.IsNullOrWhiteSpace(accountNumber) ? null : accountNumber;
        iban = string.IsNullOrWhiteSpace(iban) ? null : iban;

        if (accountNumber is null && iban is null)
        {
            return BankAccountParseResult.Fail(
                BankAccountErrors.InvalidAccountNumber, "account_number", "Enter an account number or an IBAN.");
        }

        CzechBankAccount? fromDomestic = null;
        if (accountNumber is not null)
        {
            var domestic = ParseDomestic(accountNumber);
            if (domestic.Error is not null)
            {
                return domestic;
            }

            fromDomestic = domestic.Account;
        }

        CzechBankAccount? fromIban = null;
        if (iban is not null)
        {
            var international = ParseIban(iban);
            if (international.Error is not null)
            {
                return international;
            }

            fromIban = international.Account;
        }

        if (fromDomestic is not null && fromIban is not null && fromDomestic != fromIban)
        {
            return BankAccountParseResult.Fail(
                BankAccountErrors.AccountMismatch, "iban", "The IBAN does not identify the same account as the account number.");
        }

        return BankAccountParseResult.Ok((fromDomestic ?? fromIban)!);
    }

    /// <summary><c>[prefix-]number/bank_code</c>, e.g. <c>19-2000145399/0800</c>. Spaces are ignored.</summary>
    public static BankAccountParseResult ParseDomestic(string value)
    {
        const string field = "account_number";
        var compact = RemoveWhitespace(value);

        var slash = compact.IndexOf('/');
        if (slash < 0 || slash != compact.LastIndexOf('/'))
        {
            return Invalid(field, "The account number must be in the form [prefix-]number/bank code, e.g. 19-2000145399/0800.");
        }

        var bankCode = compact[(slash + 1)..];
        var account = compact[..slash];
        var dash = account.IndexOf('-');
        if (dash != account.LastIndexOf('-'))
        {
            return Invalid(field, "The account number may contain at most one prefix separator '-'.");
        }

        var prefix = dash < 0 ? "" : account[..dash];
        var number = dash < 0 ? account : account[(dash + 1)..];

        if (bankCode.Length != 4 || !IsDigits(bankCode))
        {
            return Invalid(field, "The bank code must have exactly 4 digits.");
        }

        if (prefix.Length > 6 || !IsDigits(prefix) || (dash >= 0 && prefix.Length == 0))
        {
            return Invalid(field, "The account prefix must have 1 to 6 digits.");
        }

        if (number.Length is < 2 or > 10 || !IsDigits(number))
        {
            return Invalid(field, "The account number must have 2 to 10 digits.");
        }

        return Validate(prefix.PadLeft(6, '0'), number.PadLeft(10, '0'), bankCode, field);
    }

    /// <summary>A Czech IBAN, electronic or printed form (<c>CZ65 0800 0000 1920 0014 5399</c>).</summary>
    public static BankAccountParseResult ParseIban(string value)
    {
        const string field = "iban";
        var compact = RemoveWhitespace(value).ToUpperInvariant();

        if (compact.Length >= 2 && char.IsAsciiLetter(compact[0]) && char.IsAsciiLetter(compact[1])
            && !compact.StartsWith(CountryCode, StringComparison.Ordinal))
        {
            return BankAccountParseResult.Fail(
                BankAccountErrors.UnsupportedCountry, field, "Only Czech bank accounts (IBAN starting with CZ) are supported.");
        }

        if (compact.Length != IbanLength || !compact.StartsWith(CountryCode, StringComparison.Ordinal) || !IsDigits(compact[2..]))
        {
            return BankAccountParseResult.Fail(
                BankAccountErrors.InvalidIban, field, "A Czech IBAN has CZ followed by 22 digits.");
        }

        if (Mod97(compact[4..] + compact[..4]) != 1)
        {
            return BankAccountParseResult.Fail(BankAccountErrors.InvalidIban, field, "The IBAN check digits are invalid.");
        }

        var bban = compact[4..];
        var result = Validate(bban[4..10], bban[10..], bban[..4], field);
        return result.Error is { Code: BankAccountErrors.InvalidAccountNumber } error
            ? BankAccountParseResult.Fail(BankAccountErrors.InvalidIban, field, error.Message)
            : result;
    }

    private static BankAccountParseResult Validate(string prefix, string number, string bankCode, string field)
    {
        if (number.TrimStart('0').Length < 2)
        {
            return Invalid(field, "The account number must have at least 2 significant digits.");
        }

        if (WeightedSum(prefix, PrefixWeights) % 11 != 0)
        {
            return Invalid(field, "The account prefix has an invalid check digit.");
        }

        if (WeightedSum(number, NumberWeights) % 11 != 0)
        {
            return Invalid(field, "The account number has an invalid check digit.");
        }

        return BankAccountParseResult.Ok(new CzechBankAccount(prefix, number, bankCode));
    }

    private static BankAccountParseResult Invalid(string field, string message) =>
        BankAccountParseResult.Fail(BankAccountErrors.InvalidAccountNumber, field, message);

    private static int WeightedSum(string digits, int[] weights)
    {
        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            sum += (digits[i] - '0') * weights[i];
        }

        return sum;
    }

    /// <summary>ISO 13616 remainder: letters become 10..35, then the whole number is taken mod 97.</summary>
    private static int Mod97(string value)
    {
        var digits = new StringBuilder();
        foreach (var c in value)
        {
            digits.Append(char.IsAsciiLetter(c) ? (c - 'A' + 10).ToString(CultureInfo.InvariantCulture) : c);
        }

        var remainder = 0;
        foreach (var c in digits.ToString())
        {
            remainder = (remainder * 10 + (c - '0')) % 97;
        }

        return remainder;
    }

    private static bool IsDigits(string value) => value.All(char.IsAsciiDigit);

    private static string RemoveWhitespace(string value) => string.Concat(value.Where(c => !char.IsWhiteSpace(c)));
}

public static class BankAccountErrors
{
    public const string InvalidAccountNumber = "invalid_account_number";
    public const string InvalidIban = "invalid_iban";
    public const string AccountMismatch = "account_mismatch";
    public const string UnsupportedCountry = "unsupported_country";
    public const string UnsupportedCurrency = "unsupported_currency";
}

/// <param name="Field">The request field the error belongs to, so a form can show it next to the input.</param>
public sealed record BankAccountError(string Code, string Field, string Message);

public sealed record BankAccountParseResult(CzechBankAccount? Account, BankAccountError? Error)
{
    public static BankAccountParseResult Ok(CzechBankAccount account) => new(account, null);

    public static BankAccountParseResult Fail(string code, string field, string message) =>
        new(null, new BankAccountError(code, field, message));
}
