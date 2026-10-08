using Pairrot.Core.Inbound;

namespace Pairrot.Core.Webhooks;

/// <summary>
/// A tenant's single outgoing payment-status webhook (Feature 010). The owner is the subscriber;
/// with <see cref="WebhookScope.OwnAndDirectChildren"/> it also receives its direct children's
/// payment changes. It starts paused, and its URL is versioned as destination revisions.
/// </summary>
public class WebhookEndpoint
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public WebhookScope Scope { get; set; }
    public WebhookEndpointState State { get; set; } = WebhookEndpointState.Paused;

    /// <summary>Set at creation, immutable. Real payment events are routed only to the same environment.</summary>
    public InboundEnvironment Environment { get; set; }

    /// <summary>The current destination; incremented on every URL change.</summary>
    public int DestinationRevision { get; set; } = 1;

    /// <summary>The current revision's URL, for reading; the revision rows keep every URL used. <c>null</c> once erased.</summary>
    public string? Url { get; set; }

    public DateTimeOffset? LastSuccessAt { get; set; }
    public DateTimeOffset? LastTestAcceptedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Exposed as the ETag; incremented on every configuration change.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Feature 014: disabled for good with its erased tenant; the URL and the secrets are gone.</summary>
    public DateTimeOffset? ErasedAt { get; set; }
}

public enum WebhookScope
{
    Own,
    OwnAndDirectChildren,
}

public enum WebhookEndpointState
{
    Paused,
    Active,
}

/// <summary>An immutable destination of an endpoint; attempts keep the revision they were sent to.</summary>
public class WebhookDestinationRevision
{
    public Guid Id { get; set; }
    public Guid EndpointId { get; set; }
    public int Revision { get; set; }

    /// <summary><c>null</c> once the endpoint's tenant was erased.</summary>
    public string? Url { get; set; }

    public required string CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// An endpoint's HMAC signing key. The secret is kept encrypted (it must be recoverable to sign);
/// a retired key loses its secret but keeps its metadata. At most one current and one retiring key.
/// </summary>
public class WebhookSigningKey
{
    public Guid Id { get; set; }
    public Guid EndpointId { get; set; }

    /// <summary>The public key ID sent in the signature header, e.g. <c>pwhk_…</c>.</summary>
    public required string KeyId { get; set; }

    /// <summary>The secret protected by the application's key ring; <c>null</c> once retired.</summary>
    public string? ProtectedSecret { get; set; }

    public WebhookKeyState State { get; set; } = WebhookKeyState.Current;
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>For a retiring key: when it stops signing.</summary>
    public DateTimeOffset? RetiresAt { get; set; }

    public DateTimeOffset? RetiredAt { get; set; }
}

public enum WebhookKeyState
{
    Current,
    Retiring,
    Retired,
}

/// <summary>
/// The immutable serialized event. A real event has the ID of the Feature 009 payment change it
/// describes; retries and replays send exactly these bytes, never a rebuilt body.
/// </summary>
public class WebhookEvent
{
    public Guid Id { get; set; }
    public required string Type { get; set; }
    public int SchemaVersion { get; set; } = 1;

    /// <summary>The payment owner (real events) or the endpoint owner (tests).</summary>
    public Guid TenantId { get; set; }

    public Guid? PaymentChangeId { get; set; }
    public Guid? PaymentRequestId { get; set; }
    public int? Sequence { get; set; }
    public InboundEnvironment Environment { get; set; }

    /// <summary>A <c>webhook.test</c> probe of one endpoint; never a payment change.</summary>
    public bool Synthetic { get; set; }

    /// <summary>The exact bytes sent; <c>null</c> once the payment owner was erased. Never rebuilt.</summary>
    public string? Body { get; set; }

    /// <summary>The digest of the original body; kept after erasure.</summary>
    public required string BodySha256 { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Feature 014: the body was purged with its owner; the event cannot be sent again.</summary>
    public DateTimeOffset? PayloadErasedAt { get; set; }
}

/// <summary>
/// One event for one endpoint, unique per pair. Delivery state is independent of payment state:
/// failing to deliver never changes a payment.
/// </summary>
public class WebhookDelivery
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public Guid EndpointId { get; set; }

    /// <summary>The endpoint's owner.</summary>
    public Guid SubscriberTenantId { get; set; }

    /// <summary>The event's payment owner: the subscriber itself or a direct child.</summary>
    public Guid OwnerTenantId { get; set; }

    /// <summary>The destination this generation is bound to.</summary>
    public int DestinationRevision { get; set; }

    public WebhookDeliveryState State { get; set; } = WebhookDeliveryState.Pending;

    /// <summary>Why it is held, e.g. <c>destination_changed</c> or <c>tenant_not_active</c>.</summary>
    public string? HoldReason { get; set; }

    /// <summary>1 for the first sequence of attempts; a manual retry or replay starts the next.</summary>
    public int Generation { get; set; } = 1;

    /// <summary>Attempts reserved in the current generation (at most <see cref="WebhookRetryPolicy.MaxAttempts"/>).</summary>
    public int AttemptInGeneration { get; set; }

    public int TotalAttempts { get; set; }

    /// <summary>When the next attempt may start; <c>null</c> when none is scheduled.</summary>
    public DateTimeOffset? DueAt { get; set; }

    public string? LastOutcome { get; set; }
    public string? LastErrorCode { get; set; }
    public string? LastErrorMessage { get; set; }
    public int? LastHttpStatus { get; set; }
    public string? AbandonReason { get; set; }

    public Guid? LeaseToken { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public int Version { get; set; } = 1;
}

public enum WebhookDeliveryState
{
    Pending,
    Running,
    RetryWait,
    Delivered,
    Failed,
    Held,
    Abandoned,
}

/// <summary>One HTTP attempt, recorded before the request is sent. Immutable once completed. Never holds a secret.</summary>
public class WebhookDeliveryAttempt
{
    public Guid Id { get; set; }
    public Guid DeliveryId { get; set; }
    public int Generation { get; set; }

    /// <summary>1–4 within the generation.</summary>
    public int AttemptNumber { get; set; }

    public int DestinationRevision { get; set; }

    /// <summary><c>null</c> once the endpoint's tenant was erased.</summary>
    public string? Url { get; set; }

    /// <summary>The key IDs that signed it, space separated.</summary>
    public required string KeyIds { get; set; }

    /// <summary>The signature timestamp (Unix seconds).</summary>
    public long SignedTimestamp { get; set; }

    public WebhookAttemptOutcome Outcome { get; set; } = WebhookAttemptOutcome.InProgress;
    public int? HttpStatus { get; set; }
    public string? ErrorCode { get; set; }
    public string? ResponseExcerpt { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int? DurationMs { get; set; }
    public Guid LeaseToken { get; set; }
}

public enum WebhookAttemptOutcome
{
    InProgress,
    Delivered,

    /// <summary>Network failure, timeout, 408, 425, 429, or 5xx.</summary>
    RetryableFailure,

    /// <summary>Another 4xx, or a redirect.</summary>
    PermanentFailure,

    /// <summary>The worker's lease expired with no recorded result; the receiver may have accepted it.</summary>
    OutcomeUnknown,
}
