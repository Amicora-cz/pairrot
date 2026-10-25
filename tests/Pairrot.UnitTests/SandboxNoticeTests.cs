using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Pairrot.Core.Banking;
using Pairrot.Core.Inbound;
using Pairrot.Core.Parsing;
using Pairrot.Infrastructure.Sandbox;

namespace Pairrot.UnitTests;

/// <summary>Feature 021: every sandbox renderer is parsed back by its own bank profile, and the sandbox profile is as strict as a real one.</summary>
public class SandboxNoticeTests
{
    private static readonly SenderAuthentication Passing = new("pass", "pass", "pass", "sandbox", null);
    private static readonly CzechBankAccount Payer = CzechBankAccount.ParseDomestic("19-2000145399/0100").Account!;

    public static TheoryData<string, string> Renderers => new()
    {
        { CeskaSporitelnaProfile.ProfileName, "0800" },
        { SandboxBankProfile.ProfileName, "0100" },
    };

    private static (ISandboxNoticeRenderer Renderer, IBankProfile Profile) For(string name) => name == CeskaSporitelnaProfile.ProfileName
        ? (new CeskaSporitelnaRenderer(), new CeskaSporitelnaProfile())
        : (new SandboxProfileRenderer(), new SandboxBankProfile());

    private static CzechBankAccount Destination(string bankCode) => new("000019", "2000145399", bankCode);

    private static ParseEvaluation Evaluate(
        IBankProfile profile, SandboxNotice notice, CzechBankAccount destination, string? from = null, SenderAuthentication? auth = null) =>
        BankMessageEvaluator.Evaluate(profile, new BankMessage(
            from ?? notice.HeaderFrom, notice.Subject, notice.Text, notice.Html, auth ?? Passing, InboundSourceKind.Received,
            InboundEnvironment.Test, DateTimeOffset.Parse("2026-10-25T08:00:00Z"), destination, "CZK"));

    [Theory]
    [MemberData(nameof(Renderers))]
    public void Every_field_combination_round_trips_through_its_profile(string profileName, string bankCode)
    {
        var (renderer, profile) = For(profileName);
        var destination = Destination(bankCode);
        var references = renderer.SupportsBankReference ? new string?[] { null, "SBX-0001" } : [null];
        long[] amounts = [1, 99, 100, 12345, 100_000, 123_456_789, 999_999_999];
        string?[] symbols = [null, "1", "0000000123", "2026000001", "9999999999"];
        string?[] messages = [null, "Lekce 12. 11.", "Platba <b>&amp;</b>: \"díky\"", "řádek\nzápis: 1"];
        string[] names = ["Sandbox Plátce", "Jana Nováková", "O'Brien & Syn <s.r.o.>"];
        DateTimeOffset[] times =
        [
            DateTimeOffset.Parse("2026-10-25T09:15:00+01:00"), DateTimeOffset.Parse("2026-07-01T23:59:00+02:00"),
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"), DateTimeOffset.Parse("2026-10-24T22:30:00Z"),
        ];

        var checkedCount = 0;
        foreach (var amount in amounts)
        foreach (var symbol in symbols)
        foreach (var message in messages)
        foreach (var reference in references)
        {
            var name = names[checkedCount % names.Length];
            var time = times[checkedCount % times.Length];
            checkedCount++;
            var payment = new SandboxPayment(destination, "CZK", amount, time, Payer, name, symbol, message, reference);
            var result = Evaluate(profile, renderer.Render(payment), destination);

            Assert.Equal((ParseOutcome.Parsed, TrustDecision.Trusted), (result.Outcome, result.Trust!.Decision));
            var c = result.Candidate!;
            Assert.Equal((amount, "CZK", symbol), (c.Amount, c.Currency, c.VariableSymbol));
            Assert.Equal(DestinationEvidence.FullMatch, c.Destination);
            Assert.Equal(Payer.AccountNumber, c.SenderAccount);
            Assert.Equal(name, c.SenderName);
            Assert.Equal(message?.Replace('\n', ' '), c.Message);
            Assert.Equal(reference, c.BankReference);
            var prague = TimeZoneInfo.ConvertTime(time, TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague"));
            Assert.Equal((DateOnly.FromDateTime(prague.DateTime), prague.DateTime.ToString("yyyy-MM-dd HH:mm")),
                (c.BookedOn!.Value, c.OccurredAt!.Value.DateTime.ToString("yyyy-MM-dd HH:mm")));
        }

        Assert.True(checkedCount > 100);
    }

    [Theory]
    [MemberData(nameof(Renderers))]
    public void A_debit_is_recognized_and_is_not_a_payment(string profileName, string bankCode)
    {
        var (renderer, profile) = For(profileName);
        var destination = Destination(bankCode);
        var notice = renderer.Render(new SandboxPayment(destination, "CZK", 4500, DateTimeOffset.UtcNow, Payer, "Plátce", "1", Incoming: false));

        var result = Evaluate(profile, notice, destination);

        Assert.Equal((ParseOutcome.NotPayment, ParseReasons.OutgoingTransfer), (result.Outcome, result.PrimaryReason.Code));
    }

    [Fact]
    public void The_sandbox_profile_trusts_only_its_own_sender_with_a_passing_dmarc()
    {
        var (renderer, profile) = For(SandboxBankProfile.ProfileName);
        var destination = Destination("0100");
        var notice = renderer.Render(new SandboxPayment(destination, "CZK", 4500, DateTimeOffset.UtcNow, Payer, "Plátce", "1"));

        Assert.Equal(TrustDecision.Trusted, Evaluate(profile, notice, destination).Trust!.Decision);

        var stranger = Evaluate(profile, notice, destination, from: "notifikace@banka.example.com");
        Assert.Equal((ParseOutcome.ReviewRequired, TrustDecision.Untrusted, ParseReasons.SenderNotAllowed),
            (stranger.Outcome, stranger.Trust!.Decision, stranger.PrimaryReason.Code));

        foreach (var auth in new[]
        {
            SenderAuthentication.Unknown,
            new SenderAuthentication("pass", "pass", "fail", "sandbox", null),
            new SenderAuthentication("pass", "pass", "pass", null, null),
        })
        {
            var result = Evaluate(profile, notice, destination, auth: auth);
            Assert.Equal((ParseOutcome.ReviewRequired, TrustDecision.Untrusted), (result.Outcome, result.Trust!.Decision));
        }
    }

    [Fact]
    public void The_sandbox_profile_does_not_recognize_other_text_or_a_changed_destination()
    {
        var (renderer, profile) = For(SandboxBankProfile.ProfileName);
        var destination = Destination("0100");
        var notice = renderer.Render(new SandboxPayment(destination, "CZK", 4500, DateTimeOffset.UtcNow, Payer, "Plátce", "1"));

        Assert.Equal(ParseOutcome.Unsupported, Evaluate(profile, notice with { Text = "Vážený kliente, změnili jsme podmínky." }, destination).Outcome);
        Assert.Equal(ParseOutcome.Unsupported, Evaluate(profile, notice with { Text = notice.Text!.Replace("PAIRROT SANDBOX NOTICE", "SOMETHING ELSE") }, destination).Outcome);
        var other = Evaluate(profile, notice, Destination("0100") with { Number = "0000000019" });
        Assert.Equal(ParseReasons.DestinationMismatch, other.PrimaryReason.Code);
    }

    [Fact]
    public void Line_breaks_in_values_cannot_forge_another_labeled_line()
    {
        var (renderer, profile) = For(SandboxBankProfile.ProfileName);
        var destination = Destination("0100");
        var payment = new SandboxPayment(destination, "CZK", 4500, DateTimeOffset.UtcNow, Payer, "Plátce\nAmount: 1,00 CZK", "1", "x\nReference: FORGED");

        var candidate = Evaluate(profile, renderer.Render(payment), destination).Candidate!;

        Assert.Equal((4500L, null), (candidate.Amount, candidate.BankReference));
    }

    [Fact]
    public void The_registry_uses_the_sandbox_profile_only_as_a_fallback_for_banks_without_one()
    {
        var real = new BankProfileRegistry([new CeskaSporitelnaProfile()]);
        var sandbox = new BankProfileRegistry([new CeskaSporitelnaProfile()], new SandboxBankProfile());

        Assert.Null(real.For(Destination("0100")));
        Assert.Equal(SandboxBankProfile.ProfileName, sandbox.For(Destination("0100"))!.Name);
        Assert.Equal(CeskaSporitelnaProfile.ProfileName, sandbox.For(Destination("0800"))!.Name);
        Assert.Equal([CeskaSporitelnaProfile.ProfileName], real.Installed.Select(p => p.Name));
        Assert.Equal([CeskaSporitelnaProfile.ProfileName, SandboxBankProfile.ProfileName], sandbox.Installed.Select(p => p.Name));
    }

    [Theory]
    [InlineData("Development", true, true)]
    [InlineData("Testing", true, true)]
    [InlineData("Production", true, false)]
    [InlineData("Staging", true, false)]
    [InlineData("Production", false, true)]
    public void Sandbox_mode_is_allowed_only_in_development_and_testing(string environment, bool enabled, bool allowed)
    {
        var result = new SandboxOptionsValidator(new FakeEnvironment(environment)).Validate(null, new SandboxOptions { Enabled = enabled });

        Assert.Equal(allowed, result.Succeeded);
        if (!allowed)
        {
            Assert.Equal("Sandbox:Enabled is only allowed in Development and Testing; sandbox payments are never real.", result.FailureMessage);
        }
    }

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Pairrot";
        public string ContentRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
