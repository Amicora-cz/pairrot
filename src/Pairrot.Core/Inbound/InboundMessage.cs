using Pairrot.Core.Parsing;

namespace Pairrot.Core.Inbound;

/// <summary>
/// One received email, identified by its provider instance and source message ID. Ownership comes
/// only from the adapter's recipient evidence and Pairrot's address map; an unresolved message has
/// no tenant and is never visible to tenants. Receiving a message changes no payment.
/// </summary>
public class InboundMessage
{
    public Guid Id { get; set; }
    public required string ProviderInstanceId { get; set; }
    public required string SourceMessageId { get; set; }

    /// <summary>From the instance's trusted configuration, never from the delivery.</summary>
    public InboundSourceKind SourceKind { get; set; }

    public InboundEnvironment Environment { get; set; }

    public InboundRoutingState RoutingState { get; set; }
    public Guid? TenantId { get; set; }
    public Guid? BankConnectionId { get; set; }
    public Guid? InboundAddressId { get; set; }

    /// <summary>Why bank processing is held for a routed message; <c>null</c> when it may proceed.</summary>
    public InboundHoldReason? HoldReason { get; set; }

    /// <summary>
    /// Feature 008: who the delivery metadata says sent it, decided before any content is retrieved.
    /// <c>null</c> for messages that were never routed or were received before Feature 008.
    /// It limits what is collected; it is not a trust decision.
    /// </summary>
    public InboundSenderClass? SenderClass { get; set; }

    /// <summary>The From domain, kept even when everything else about a foreign message is dropped.</summary>
    public string? SenderDomain { get; set; }

    /// <summary>When this message is deleted (foreign mail metadata and forwarding confirmations); <c>null</c> to keep it.</summary>
    public DateTimeOffset? RetainUntil { get; set; }

    /// <summary>Normalized delivered recipients used for routing.</summary>
    public List<string> EnvelopeRecipients { get; set; } = [];

    /// <summary>Provenance of <see cref="EnvelopeRecipients"/>.</summary>
    public required string RecipientEvidence { get; set; }

    public string? EnvelopeSender { get; set; }
    public string? HeaderFrom { get; set; }
    public string? Subject { get; set; }
    public required string TransportKind { get; set; }
    public required string DeliveryEvidence { get; set; }

    /// <summary>Provider-specific diagnostic data (JSON) from the first delivery. Never read by parsers.</summary>
    public string? TransportMetadata { get; set; }

    /// <summary>When the receiving system received the email, or when Pairrot accepted it if it did not say.</summary>
    public DateTimeOffset ReceivedAt { get; set; }

    public InboundContentMode ContentMode { get; set; }

    /// <summary>Deferred mode: the opaque reference the instance's content source retrieves.</summary>
    public string? ContentReference { get; set; }

    public InboundContentState ContentState { get; set; } = InboundContentState.Pending;

    /// <summary>When the common snapshot below was saved.</summary>
    public DateTimeOffset? ContentStoredAt { get; set; }

    public string? ContentSha256 { get; set; }
    public string? TextBody { get; set; }
    public string? HtmlBody { get; set; }

    /// <summary>Sender-supplied headers (JSON object). Untrusted.</summary>
    public string? Headers { get; set; }

    /// <summary>Recipients the content reported, compared with the routing evidence.</summary>
    public List<string>? ContentRecipients { get; set; }

    /// <summary>Attachment metadata (JSON array).</summary>
    public string? Attachments { get; set; }

    /// <summary>The provider's original content response, for diagnosis only.</summary>
    public string? RawProviderContent { get; set; }

    public string SpfResult { get; set; } = AuthenticationResults.Unknown;
    public string DkimResult { get; set; } = AuthenticationResults.Unknown;
    public string DmarcResult { get; set; } = AuthenticationResults.Unknown;
    public string? AuthenticationOrigin { get; set; }
    public string? AuthenticationRaw { get; set; }

    /// <summary>The latest content error, cleared when content is stored. Never contains secrets.</summary>
    public string? FetchErrorCode { get; set; }

    public string? FetchErrorMessage { get; set; }
    public DateTimeOffset? FetchErrorAt { get; set; }

    /// <summary>The latest completed parse run (Feature 007), separate from routing and content states.</summary>
    public Guid? LatestParseRunId { get; set; }

    public ParseOutcome? LatestParseOutcome { get; set; }
    public DateTimeOffset? ParsedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public enum InboundSourceKind
{
    /// <summary>Received by a configured provider instance.</summary>
    Received,

    /// <summary>A test fixture: proves transport handling, never bank origin or live reception.</summary>
    Fixture,
}

public enum InboundContentMode
{
    /// <summary>The delivery carried a reference; content is retrieved by a background job.</summary>
    Deferred,

    /// <summary>The delivery carried the complete content; no retrieval job exists.</summary>
    Inline,
}

public enum InboundRoutingState
{
    Routed,

    /// <summary>No recipient is a known address. Quarantined; visible only to operators.</summary>
    UnknownRecipient,

    /// <summary>Recipients belong to more than one bank connection. Quarantined; never copied to several tenants.</summary>
    AmbiguousRecipient,

    /// <summary>A recipient is bound to another provider instance than the one that delivered it. Quarantined.</summary>
    InstanceMismatch,

    /// <summary>The transport supplied no authoritative recipient evidence. Quarantined rather than guessed.</summary>
    MissingRecipientEvidence,
}

public enum InboundHoldReason
{
    /// <summary>The message was sent to an address that has since been replaced.</summary>
    AddressRetired,

    BankConnectionArchived,

    /// <summary>The owner or its parent is suspended or deleted.</summary>
    TenantNotActive,

    /// <summary>The content names different recipients than the routing evidence; the owner is not changed.</summary>
    RecipientMismatch,

    /// <summary>Feature 008: the address was paused for foreign mail; only minimal metadata is kept.</summary>
    AddressPaused,
}

/// <summary>Whether the source content is stored. Not whether it was parsed: see <see cref="InboundMessage.LatestParseOutcome"/>.</summary>
public enum InboundContentState
{
    Pending,
    Available,
    Failed,

    /// <summary>Deliberately not retrieved or stored: foreign mail or a paused address (Feature 008).</summary>
    Skipped,

    /// <summary>Feature 014: purged by the payer-data retention limit. Terminal; never retrieved again.</summary>
    Expired,

    /// <summary>Feature 014: purged with its erased tenant. Terminal and takes precedence over <see cref="Expired"/>.</summary>
    Erased,
}

/// <summary>Feature 008 sender classes, from delivery metadata only.</summary>
public enum InboundSenderClass
{
    /// <summary>From matches the connection's bank profile; still subject to the parser's origin checks.</summary>
    Bank,

    /// <summary>A configured mail provider asking to confirm forwarding; shown to the tenant, never parsed.</summary>
    ForwardingConfirmation,

    /// <summary>From is a registered forwarding mailbox of the connection (a manual forward).</summary>
    TenantForward,

    /// <summary>Anything else: not retrieved, not stored beyond minimal metadata, and counted toward a pause.</summary>
    Foreign,
}

/// <summary>An accepted transport delivery. Repeated deliveries with the same identity share a row.</summary>
public class InboundDeliveryReceipt
{
    public Guid Id { get; set; }
    public required string ProviderInstanceId { get; set; }
    public required string SourceDeliveryId { get; set; }
    public required string TransportKind { get; set; }
    public required string DeliveryEvidence { get; set; }

    /// <summary>The transport payload as received, if any.</summary>
    public string? RawPayload { get; set; }

    /// <summary>Digest of the delivery's content, to tell a harmless repeat from a conflicting reuse of its ID.</summary>
    public required string PayloadSha256 { get; set; }

    public InboundReceiptOutcome Outcome { get; set; }
    public Guid InboundMessageId { get; set; }
    public DateTimeOffset AcceptedAt { get; set; }
}

public enum InboundReceiptOutcome
{
    /// <summary>A new message was recorded.</summary>
    MessageCreated,

    /// <summary>An earlier delivery already recorded the same email.</summary>
    ExistingMessage,
}
