namespace Pairrot.Core.Inbound;

/// <summary>
/// The common intake contract. A delivery adapter builds it only after authenticating its own
/// transport (a signed webhook, a trusted SMTP handoff, ...). It is internal: no tenant caller can
/// submit one. Source kind and environment are not part of it; intake takes them from the trusted
/// configuration of <see cref="ProviderInstanceId"/>.
/// </summary>
public sealed record InboundDelivery
{
    public required string ProviderInstanceId { get; init; }

    /// <summary>Stable identity of this transport delivery (e.g. a verified webhook event ID); the same on every retry.</summary>
    public required string SourceDeliveryId { get; init; }

    /// <summary>Stable identity of the email within the instance; never the sender-controlled RFC Message-ID.</summary>
    public required string SourceMessageId { get; init; }

    /// <summary>Reception time reported by the trusted receiving system, if it reports one.</summary>
    public DateTimeOffset? ReceivedAt { get; init; }

    /// <summary>Delivered recipients as the adapter authenticated them. Empty when the transport has no reliable recipient evidence.</summary>
    public IReadOnlyList<string> EnvelopeRecipients { get; init; } = [];

    /// <summary>Where <see cref="EnvelopeRecipients"/> came from, e.g. <c>resend_webhook:to,cc,bcc</c>.</summary>
    public required string RecipientEvidence { get; init; }

    public string? EnvelopeSender { get; init; }

    /// <summary>The From header. Not authenticated by itself.</summary>
    public string? HeaderFrom { get; init; }

    public string? Subject { get; init; }

    /// <summary>The transport, e.g. <c>resend_webhook</c> or <c>smtp</c>.</summary>
    public required string TransportKind { get; init; }

    /// <summary>How the adapter authenticated the transport, e.g. <c>svix_signature</c>.</summary>
    public required string DeliveryEvidence { get; init; }

    /// <summary>The transport payload exactly as received, if the transport has one; kept for diagnosis.</summary>
    public string? RawTransportPayload { get; init; }

    /// <summary>Provider-specific diagnostic data (JSON), never read by parsers.</summary>
    public string? TransportMetadata { get; init; }

    /// <summary>Sender authentication known at delivery; content retrieved later may supply it instead.</summary>
    public SenderAuthentication? SenderAuthentication { get; init; }

    /// <summary>Inline delivery: the complete source. Exactly one of this and <see cref="ContentReference"/> is set.</summary>
    public InboundContentSnapshot? Content { get; init; }

    /// <summary>Deferred delivery: an opaque reference that this instance's content source can retrieve.</summary>
    public string? ContentReference { get; init; }
}

/// <summary>
/// The common source snapshot every adapter produces, inline or after retrieval. Bank parsers read
/// only this, never provider-specific JSON.
/// </summary>
/// <param name="HeadersJson">Sender-supplied headers as a JSON object. Untrusted: anyone can write any header.</param>
/// <param name="DeliveredRecipients">The adapter's recipient evidence at content time, compared with the routing evidence; <c>null</c> when it has none.</param>
/// <param name="AttachmentsJson">Attachment metadata as a JSON array. Attachments themselves are never downloaded.</param>
/// <param name="RawProviderContent">The provider's original content response, for diagnosis only.</param>
public sealed record InboundContentSnapshot(
    string? Text,
    string? Html,
    string? HeadersJson,
    IReadOnlyList<string>? DeliveredRecipients,
    string? AttachmentsJson,
    SenderAuthentication Authentication,
    string? RawProviderContent = null)
{
    /// <summary>Size counted against the common snapshot limit (UTF-8 bytes of the stored common fields).</summary>
    public long Size =>
        Bytes(Text) + Bytes(Html) + Bytes(HeadersJson) + Bytes(AttachmentsJson) + Bytes(Authentication.RawJson)
        + (DeliveredRecipients?.Sum(r => (long)Bytes(r)) ?? 0);

    private static int Bytes(string? value) => value is null ? 0 : System.Text.Encoding.UTF8.GetByteCount(value);
}

/// <summary>
/// SPF, DKIM, and DMARC results computed by the receiving system, normalized to
/// <see cref="AuthenticationResults.Values"/>. Missing or unrecognized results are <c>unknown</c>,
/// never a pass. Never taken from an <c>Authentication-Results</c> header inside the email.
/// </summary>
/// <param name="Origin">Which system computed the results, e.g. <c>resend_receiving_api</c>; <c>null</c> when unknown.</param>
/// <param name="RawJson">The results as the system reported them.</param>
public sealed record SenderAuthentication(string Spf, string Dkim, string Dmarc, string? Origin, string? RawJson)
{
    public static readonly SenderAuthentication Unknown = new(
        AuthenticationResults.Unknown, AuthenticationResults.Unknown, AuthenticationResults.Unknown, null, null);
}

public static class AuthenticationResults
{
    public const string Unknown = "unknown";

    /// <summary>RFC 8601 result names, plus <c>unknown</c> for missing or unrecognized evidence.</summary>
    public static readonly IReadOnlySet<string> Values =
        new HashSet<string> { "pass", "fail", "softfail", "neutral", "none", "temperror", "permerror", "policy", Unknown };

    public static string Normalize(string? value) =>
        value?.Trim().ToLowerInvariant() is { } normalized && Values.Contains(normalized) ? normalized : Unknown;
}
