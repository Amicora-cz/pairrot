using System.Net.Mail;
using Pairrot.Core.Inbound;

namespace Pairrot.Core.Parsing;

public enum TrustDecision
{
    /// <summary>An allowed bank sender with passing DMARC computed by the receiving system.</summary>
    Trusted,

    Untrusted,
}

/// <summary>The facts the trust decision used, stored with every run.</summary>
public sealed record TrustEvidence(
    string? FromAddress,
    string? FromDomain,
    string Spf,
    string Dkim,
    string Dmarc,
    string? AuthenticationOrigin,
    string SourceKind,
    string Environment);

public sealed record TrustAssessment(TrustDecision Decision, IReadOnlyList<ParseReason> Reasons, TrustEvidence Evidence);

/// <summary>
/// The initial conservative origin policy: the From address must be an allowed bank identity and
/// the receiving system must report a passing DMARC result, which ties that From domain to SPF or
/// DKIM. Missing, weaker, or indeterminate evidence is untrusted. Display names, forwarded copies,
/// and <c>Authentication-Results</c> lines inside the email are never evidence. Source kind and
/// environment are recorded but do not make evidence stronger or weaker.
/// </summary>
public static class SourceTrust
{
    public static TrustAssessment Assess(BankMessage message, SenderPolicy senders)
    {
        var reasons = new List<ParseReason>();
        var from = SingleAddress(message.HeaderFrom);
        var auth = message.Authentication;
        var evidence = new TrustEvidence(
            from?.Address,
            from?.Domain,
            AuthenticationResults.Normalize(auth.Spf),
            AuthenticationResults.Normalize(auth.Dkim),
            AuthenticationResults.Normalize(auth.Dmarc),
            auth.Origin,
            message.SourceKind == InboundSourceKind.Fixture ? "fixture" : "received",
            message.Environment == InboundEnvironment.Live ? "live" : "test");

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

        if (auth.Origin is null)
        {
            reasons.Add(new ParseReason(ParseReasons.AuthenticationUnknown,
                "No receiving system reported sender authentication results."));
        }
        else if (evidence.Dmarc == AuthenticationResults.Unknown)
        {
            reasons.Add(new ParseReason(ParseReasons.AuthenticationUnknown, "The receiving system's DMARC result is unknown."));
        }
        else if (evidence.Dmarc != "pass")
        {
            reasons.Add(new ParseReason(ParseReasons.DmarcNotPass, $"DMARC is '{evidence.Dmarc}', not 'pass'."));
        }

        return new TrustAssessment(reasons.Count == 0 ? TrustDecision.Trusted : TrustDecision.Untrusted, reasons, evidence);
    }

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
