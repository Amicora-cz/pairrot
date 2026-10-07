using Pairrot.Core.Banking;
using Pairrot.Core.Inbound;
using Pairrot.Core.Parsing;
using Pairrot.Tests.Shared;

namespace Pairrot.UnitTests;

/// <summary>Feature 008 trust rules for forwarded notifications, with synthetic headers.</summary>
public class ForwardedTrustTests
{
    private static readonly CzechBankAccount Account = CzechBankAccount.ParseDomestic("19-2000145399/0800").Account!;
    private static readonly SyntheticBankProfile Profile = new();
    private const string Source = "lektor@gmail.example";

    private static BankMessage Message(string? from, string dmarc, string spf = "pass", string? envelope = null, string? origin = "receiver") =>
        new(from, "Notice", SyntheticBankProfile.Notice(("Type", "incoming")), null, new SenderAuthentication(spf, "none", dmarc, origin, null),
            InboundSourceKind.Received, InboundEnvironment.Live, DateTimeOffset.UtcNow, Account, "CZK")
        {
            EnvelopeSender = envelope,
            ForwardingSources = new HashSet<string> { Source },
            SpfVerifiedForwarderDomains = new HashSet<string> { "gmail.example" },
        };

    private static TrustAssessment Assess(BankMessage message) => SourceTrust.Assess(message, Profile.Senders);

    [Fact]
    public void Direct_bank_mail_is_trusted_by_bank_dmarc()
    {
        var trust = Assess(Message(SyntheticBankProfile.Sender, "pass", envelope: "bounce@bank.pairrot-fixture.net"));

        Assert.Equal((TrustDecision.Trusted, OriginPath.Direct, SourceTrust.BankDmarc),
            (trust.Decision, trust.Evidence.OriginPath, trust.Evidence.AuthenticatedBy));
    }

    [Theory]
    [InlineData("lektor+caf_=pairrot=in.example@gmail.example")]
    [InlineData(Source)]
    public void Automatic_forwards_are_recognized_by_the_envelope_and_trusted_by_spf_when_dmarc_broke(string envelope)
    {
        var survived = Assess(Message(SyntheticBankProfile.Sender, "pass", envelope: envelope));
        Assert.Equal((TrustDecision.Trusted, OriginPath.AutomaticForward, SourceTrust.BankDmarc),
            (survived.Decision, survived.Evidence.OriginPath, survived.Evidence.AuthenticatedBy));

        var broken = Assess(Message(SyntheticBankProfile.Sender, "fail", envelope: envelope));
        Assert.Equal((TrustDecision.Trusted, OriginPath.AutomaticForward, SourceTrust.EnvelopeSpf, Source),
            (broken.Decision, broken.Evidence.OriginPath, broken.Evidence.AuthenticatedBy, broken.Evidence.ForwardingSource));
    }

    [Theory]
    [InlineData("fail", "lektor+caf_=x@gmail.example", ParseReasons.ForwarderNotAuthenticated)] // SPF failed
    [InlineData("pass", "jiny+caf_=x@gmail.example", ParseReasons.DmarcNotPass)] // not a registered source
    [InlineData("pass", "lektor+caf_=x@other.example", ParseReasons.DmarcNotPass)] // another domain
    public void Unauthenticated_automatic_forwards_are_untrusted(string spf, string envelope, string reason)
    {
        var trust = Assess(Message(SyntheticBankProfile.Sender, "fail", spf, envelope));

        Assert.Equal(TrustDecision.Untrusted, trust.Decision);
        Assert.Contains(trust.Reasons, r => r.Code == reason);
    }

    [Fact]
    public void Spf_is_used_only_for_verified_forwarder_domains()
    {
        var unverified = Message(SyntheticBankProfile.Sender, "fail", envelope: Source) with { SpfVerifiedForwarderDomains = new HashSet<string>() };
        var trust = Assess(unverified);

        Assert.Equal(TrustDecision.Untrusted, trust.Decision);
        Assert.Contains(trust.Reasons, r => r.Code == ParseReasons.ForwarderNotAuthenticated);
    }

    [Theory]
    [InlineData("pass", "receiver", TrustDecision.Trusted)]
    [InlineData("fail", "receiver", TrustDecision.Untrusted)]
    [InlineData("unknown", "receiver", TrustDecision.Untrusted)]
    [InlineData("pass", null, TrustDecision.Untrusted)]
    public void Manual_forwards_need_dmarc_for_the_registered_mailbox(string dmarc, string? origin, TrustDecision decision)
    {
        var trust = Assess(Message($"Lektor <{Source}>", dmarc, origin: origin));

        Assert.Equal((decision, OriginPath.ManualForward, Source), (trust.Decision, trust.Evidence.OriginPath, trust.Evidence.ForwardingSource));
    }

    [Fact]
    public void Unregistered_mailboxes_are_not_forwarders()
    {
        var trust = Assess(Message("jiny@gmail.example", "pass"));

        Assert.Equal((TrustDecision.Untrusted, OriginPath.Direct), (trust.Decision, trust.Evidence.OriginPath));
        Assert.Contains(trust.Reasons, r => r.Code == ParseReasons.SenderNotAllowed);
    }

    [Fact]
    public void Manual_forwards_are_parsed_from_the_quoted_original()
    {
        var notice = SyntheticBankProfile.Notice(("Type", "incoming"), ("Account", "19-2000145399/0800"), ("Amount", "450,00 CZK"), ("Booked", "07.10.2026"));
        var text = "Ahoj, posílám.\n\n-------- Přeposlaná zpráva --------\nOd: Banka <notify@bank.pairrot-fixture.net>\nDatum: 7. 10. 2026\nPředmět: Notice\n\n"
            + string.Join('\n', notice.Split('\n').Select(l => "> " + l));
        var message = Message($"Lektor <{Source}>", "pass") with { Text = text };

        var result = BankMessageEvaluator.Evaluate(Profile, message);

        Assert.Equal(ParseOutcome.Parsed, result.Outcome);
        Assert.Equal(45000L, result.Candidate!.Amount);
        Assert.Equal(OriginPath.ManualForward, result.Trust!.Evidence.OriginPath);
    }

    [Fact]
    public void Unquote_keeps_messages_without_a_separator()
    {
        Assert.Equal("a\nb", HtmlText.Unquote("> a\n>> b"));
        Assert.Equal("body", HtmlText.Unquote("intro\n---------- Forwarded message ---------\nFrom: x\nSubject: y\n> body"));
    }
}
