using Pairrot.Core.Tenants;

namespace Pairrot.UnitTests;

public class TenantRulesTests
{
    [Theory]
    [InlineData("studio@example.com")]
    [InlineData("first.last+tag@sub.example.cz")]
    [InlineData(null)]
    public void Valid_emails_are_accepted(string? email) =>
        Assert.Null(TenantRules.ValidateContactEmail(email));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-email")]
    [InlineData("a@b")]
    [InlineData("a@@b.cz")]
    [InlineData("@example.com")]
    [InlineData("a b@example.com")]
    [InlineData("Name <a@example.com>")]
    public void Invalid_emails_are_rejected(string email) =>
        Assert.NotNull(TenantRules.ValidateContactEmail(email));

    [Fact]
    public void Overlong_email_is_rejected() =>
        Assert.NotNull(TenantRules.ValidateContactEmail(new string('a', 250) + "@x.cz"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Blank_names_are_rejected(string? name) =>
        Assert.NotNull(TenantRules.ValidateName(name));

    [Fact]
    public void Names_longer_than_limit_are_rejected()
    {
        Assert.Null(TenantRules.ValidateName(new string('a', TenantRules.MaxNameLength)));
        Assert.NotNull(TenantRules.ValidateName(new string('a', TenantRules.MaxNameLength + 1)));
    }
}
