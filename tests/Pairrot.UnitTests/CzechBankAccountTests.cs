using Pairrot.Core.Banking;

namespace Pairrot.UnitTests;

public class CzechBankAccountTests
{
    private const string Iban = "CZ6508000000192000145399";

    [Theory]
    [InlineData("19-2000145399/0800", null)]
    [InlineData(" 000019 - 2000145399 / 0800 ", null)]
    [InlineData(null, "CZ6508000000192000145399")]
    [InlineData(null, "cz65 0800 0000 1920 0014 5399")]
    [InlineData("19-2000145399/0800", "CZ65 0800 0000 1920 0014 5399")]
    public void Equivalent_inputs_produce_the_same_normalized_account(string? accountNumber, string? iban)
    {
        var result = CzechBankAccount.Parse(accountNumber, iban);

        Assert.Null(result.Error);
        Assert.Equal(Iban, result.Account!.Iban);
        Assert.Equal("19-2000145399/0800", result.Account.AccountNumber);
    }

    [Fact]
    public void Account_without_prefix_round_trips_through_its_iban()
    {
        var domestic = CzechBankAccount.Parse("2000145399/0800", null).Account!;
        var international = CzechBankAccount.Parse(null, domestic.Iban).Account!;

        Assert.Equal("2000145399/0800", international.AccountNumber);
        Assert.Equal(domestic, international);
        Assert.Matches("^CZ[0-9]{22}$", domestic.Iban);
    }

    [Theory]
    [InlineData("19-2000145398/0800")] // number check digit
    [InlineData("18-2000145399/0800")] // prefix check digit
    [InlineData("2000145399")] // no bank code
    [InlineData("2000145399/080")]
    [InlineData("2000145399/08000")]
    [InlineData("abc/0800")]
    [InlineData("1-2-2000145399/0800")]
    [InlineData("-2000145399/0800")]
    [InlineData("1234567-2000145399/0800")]
    [InlineData("12345678901/0800")]
    [InlineData("0/0800")]
    [InlineData("2000145399/0800/0800")]
    public void Invalid_domestic_numbers_are_rejected(string accountNumber)
    {
        var error = CzechBankAccount.Parse(accountNumber, null).Error;

        Assert.Equal(BankAccountErrors.InvalidAccountNumber, error?.Code);
        Assert.Equal("account_number", error!.Field);
    }

    [Theory]
    [InlineData("CZ6608000000192000145399")] // IBAN check digits
    [InlineData("CZ650800000019200014539")] // too short
    [InlineData("CZ65080000001920001453991")]
    [InlineData("CZ65 0800 0000 1920 0014 539X")]
    [InlineData("6508000000192000145399")]
    public void Invalid_ibans_are_rejected(string iban)
    {
        var error = CzechBankAccount.Parse(null, iban).Error;

        Assert.Equal(BankAccountErrors.InvalidIban, error?.Code);
        Assert.Equal("iban", error!.Field);
    }

    [Fact]
    public void Iban_with_valid_check_digits_but_invalid_domestic_checksum_is_rejected()
    {
        // 2000145398 fails the CNB mod-11 rule; give it correct IBAN check digits anyway.
        var account = new CzechBankAccount("000000", "2000145398", "0800");

        Assert.Equal(BankAccountErrors.InvalidIban, CzechBankAccount.Parse(null, account.Iban).Error?.Code);
    }

    [Theory]
    [InlineData("SK3112000000198742637541")]
    [InlineData("DE89 3704 0044 0532 0130 00")]
    public void Foreign_ibans_are_unsupported(string iban) =>
        Assert.Equal(BankAccountErrors.UnsupportedCountry, CzechBankAccount.Parse(null, iban).Error?.Code);

    [Fact]
    public void Conflicting_representations_are_rejected()
    {
        var error = CzechBankAccount.Parse("2000145399/0800", Iban).Error;

        Assert.Equal(BankAccountErrors.AccountMismatch, error?.Code);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("  ", "")]
    public void Missing_account_is_rejected(string? accountNumber, string? iban) =>
        Assert.NotNull(CzechBankAccount.Parse(accountNumber, iban).Error);
}
