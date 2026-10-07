using System.Net.Mail;
using Pairrot.Core.Inbound;

namespace Pairrot.Core.Parsing;

public enum TrustDecision
{
    /// <summary>The bank's own authenticated mail, or a verified forward from the tenant's registered mailbox.</summary>
    Trusted,

    Untrusted,
}

/// <summary>The facts the trust decision used, stored with every run.</summary>
/// <param name="ForwardingSource">The registered mailbox that forwarded it, for forwarded paths.</param>
/// <param name="AuthenticatedBy">What made it trusted: <c>bank_dmarc</c>, <c>source_dmarc</c>, or <c>envelope_spf</c>.</param>
public sealed record TrustEvidence(
    string? FromAddress,
    string? FromDomain,
    string Spf,
    string Dkim,
    string Dmarc,
    string? AuthenticationOrigin,
    string SourceKind,
    string Environment,
    OriginPath OriginPath = OriginPath.Direct,
    string? ForwardingSource = null,
    string? EnvelopeSender = null,
    string? AuthenticatedBy = null);

public sealed record TrustAssessment(TrustDecision Decision, IReadOnlyList<ParseReason> Reasons, TrustEvidence Evidence);

/// <summary>
/// Origin policy. Direct or automatically forwarded mail is trusted when its From address is an
/// allowed bank identity and the receiving system reports DMARC <c>pass</c>. Feature 008 adds two
/// forwarded paths from the tenant's registered mailbox: a manual forward whose From is that mailbox
/// with DMARC <c>pass</c> for it, and an automatic forward whose envelope sender is that mailbox with
/// SPF <c>pass</c>, for providers where a real sample proved this. Display names, quoted headers, and
/// <c>Authentication-Results</c> lines inside the email are never evidence. Source kind and
/// environment are recorded but do not make evidence stronger or weaker.
/// </summary>
public static class SourceTrust
{
    public const string BankDmarc = "bank_dmarc";
    public const string SourceDmarc = "source_dmarc";
    public const string EnvelopeSpf = "envelope_spf";

    public static TrustAssessment Assess(BankMessage message, SenderPolicy senders)
    {
        var from = SingleAddress(message.HeaderFrom);
        var auth = message.Authentication;
        var spf = AuthenticationResults.Normalize(auth.Spf);
        var dmarc = AuthenticationResults.Normalize(auth.Dmarc);
        var envelope = SingleAddress(message.EnvelopeSender);
        var evidence = new TrustEvidence(
            from?.Address,
            from?.Domain,
            spf,
            AuthenticationResults.Normalize(auth.Dkim),
            dmarc,
            auth.Origin,
            message.SourceKind == InboundSourceKind.Fixture ? "fixture" : "received",
            message.Environment == InboundEnvironment.Live ? "live" : "test",
            EnvelopeSender: envelope?.Address);

        // A manual forward: From is the tenant's registered mailbox, never the bank.
        if (from is { } sender && message.ForwardingSources.Contains(sender.Address) && !senders.Allows(sender.Address, sender.Domain))
        {
            evidence = evidence with { OriginPath = OriginPath.ManualForward, ForwardingSource = sender.Address };
            return DmarcPasses(auth, dmarc) is { } failure
                ? Untrusted(evidence, failure with { Message = $"The forwarding mailbox {sender.Address} is not authenticated: {failure.Message}" })
                : new TrustAssessment(TrustDecision.Trusted, [], evidence with { AuthenticatedBy = SourceDmarc });
        }

        var forwarder = envelope is { } env ? ForwardingSourceOf(env.Address, message.ForwardingSources) : null;
        if (forwarder is not null)
        {
            evidence = evidence with { OriginPath = OriginPath.AutomaticForward, ForwardingSource = forwarder };
        }

        var reasons = new List<ParseReason>();
        if (from is null)
        {
            reasons.Add(new ParseReason(ParseReasons.SenderInvalid, "The message has no single, valid From address."));
        }
        else if (senders.IsEmpty)
        {
            reasons.Add(new ParseReason(ParseReasons.NoVerifiedSenders,
                "The bank profile has no verified sender identities yet, so no sender is trusted."));
        }
        else if (!senders.Allows(from.Value.Address, from.Value.Domain))
        {
            reasons.Add(new ParseReason(ParseReasons.SenderNotAllowed, $"The sender {from.Value.Address} is not an allowed bank identity."));
        }

        if (reasons.Count > 0)
        {
            return Untrusted(evidence, [.. reasons]);
        }

        if (DmarcPasses(auth, dmarc) is not { } bankFailure)
        {
            return new TrustAssessment(TrustDecision.Trusted, [], evidence with { AuthenticatedBy = BankDmarc });
        }

        // An automatic forward that broke the bank's DMARC: the tenant's mailbox must be authenticated instead.
        if (forwarder is not null && envelope is { } forwardedBy)
        {
            if (auth.Origin is not null && spf == "pass" && message.SpfVerifiedForwarderDomains.Contains(forwardedBy.Domain))
            {
                return new TrustAssessment(TrustDecision.Trusted, [], evidence with { AuthenticatedBy = EnvelopeSpf });
            }

            return Untrusted(evidence, bankFailure, new ParseReason(ParseReasons.ForwarderNotAuthenticated,
                message.SpfVerifiedForwarderDomains.Contains(forwardedBy.Domain)
                    ? $"The forward from {forwarder} could not be authenticated (SPF '{spf}')."
                    : $"Forwarding from {forwardedBy.Domain} is not verified yet, so the forward cannot be authenticated."));
        }

        return Untrusted(evidence, bankFailure);
    }

    /// <summary>
    /// The registered source an envelope sender belongs to: the source itself, or its Gmail-style
    /// forwarding form <c>local+caf_=…@domain</c>.
    /// </summary>
    public static string? ForwardingSourceOf(string envelopeSender, IReadOnlySet<string> sources)
    {
        if (sources.Contains(envelopeSender))
        {
            return envelopeSender;
        }

        var at = envelopeSender.LastIndexOf('@');
        var plus = envelopeSender.IndexOf("+caf_", StringComparison.Ordinal);
        if (at <= 0 || plus <= 0 || plus > at)
        {
            return null;
        }

        var candidate = envelopeSender[..plus] + envelopeSender[at..];
        return sources.Contains(candidate) ? candidate : null;
    }

    private static ParseReason? DmarcPasses(SenderAuthentication auth, string dmarc) => (auth.Origin, dmarc) switch
    {
        (null, _) => new ParseReason(ParseReasons.AuthenticationUnknown, "No receiving system reported sender authentication results."),
        (_, AuthenticationResults.Unknown) => new ParseReason(ParseReasons.AuthenticationUnknown, "The receiving system's DMARC result is unknown."),
        (_, "pass") => null,
        _ => new ParseReason(ParseReasons.DmarcNotPass, $"DMARC is '{dmarc}', not 'pass'."),
    };

    private static TrustAssessment Untrusted(TrustEvidence evidence, params ParseReason[] reasons) =>
        new(TrustDecision.Untrusted, reasons, evidence);

    private static (string Address, string Domain)? SingleAddress(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }

        try
        {
            var addresses = new MailAddressCollection { header };
            if (addresses is not [var address])
            {
                return null;
            }

            var normalized = address.Address.Trim().ToLowerInvariant();
            var at = normalized.LastIndexOf('@');
            return at > 0 && at < normalized.Length - 1 ? (normalized, normalized[(at + 1)..]) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
