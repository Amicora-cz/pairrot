using Pairrot.Core.Payments;

namespace Pairrot.Core.Matching;

/// <summary>
/// The recorded link between an incoming transaction and the payment request it pays, always for
/// the transaction's full amount. Not a movement of money. Allocations are history: an active one
/// may be revoked, but is never deleted or moved; reassigning is a revocation and a new allocation.
/// </summary>
public class PaymentAllocation
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid BankConnectionId { get; set; }
    public Guid TransactionId { get; set; }
    public Guid PaymentRequestId { get; set; }

    /// <summary>The transaction's full amount in minor units.</summary>
    public long Amount { get; set; }

    public required string Currency { get; set; }
    public AllocationMethod Method { get; set; }

    /// <summary>The user's reason for a manual allocation; <c>null</c> for an automatic one.</summary>
    public string? Reason { get; set; }

    /// <summary><c>live</c>, <c>test</c>, or <c>fixture</c>, copied from the transaction.</summary>
    public required string Evidence { get; set; }

    public AllocationState State { get; set; } = AllocationState.Active;

    /// <summary>See <see cref="ActorLabels"/>: <c>system</c>, <c>user:{id}</c>, <c>api_key:{id}</c>, or an operator.</summary>
    public required string CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokedBy { get; set; }

    /// <summary>E.g. <c>manual</c> or <c>transaction_review_required</c>.</summary>
    public string? RevocationCode { get; set; }

    public string? RevocationReason { get; set; }
    public int Version { get; set; } = 1;
}

public enum AllocationMethod
{
    Automatic,
    Manual,
}

public enum AllocationState
{
    Active,
    Revoked,
}

/// <summary>
/// One change of a request's paid amount or status, in a per-request sequence. Immutable; the
/// durable input for payment-status delivery (Feature 010), which never undoes an allocation.
/// </summary>
public class PaymentChange
{
    /// <summary>The immutable event ID.</summary>
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }
    public Guid BankConnectionId { get; set; }
    public Guid PaymentRequestId { get; set; }

    /// <summary>1, 2, ... per request.</summary>
    public int Sequence { get; set; }

    public required string ExternalId { get; set; }
    public long ExpectedAmount { get; set; }
    public required string Currency { get; set; }

    public PaymentRequestStatus StatusBefore { get; set; }
    public long PaidBefore { get; set; }
    public long? RemainingBefore { get; set; }
    public long OverpaidBefore { get; set; }

    public PaymentRequestStatus StatusAfter { get; set; }
    public long PaidAfter { get; set; }
    public long? RemainingAfter { get; set; }
    public long OverpaidAfter { get; set; }

    public PaymentChangeCause Cause { get; set; }
    public Guid? AllocationId { get; set; }
    public Guid? TransactionId { get; set; }

    /// <summary><c>live</c>, <c>test</c>, or <c>fixture</c> for allocation changes; <c>null</c> for a cancellation.</summary>
    public string? Evidence { get; set; }

    public required string Actor { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public enum PaymentChangeCause
{
    AutomaticAllocation,
    ManualAllocation,
    AllocationRevoked,

    /// <summary>A system correction: the allocated transaction lost its eligibility.</summary>
    TransactionReviewRequired,

    Cancelled,
}

/// <summary>
/// A completed manual command, keyed by the caller's <c>Idempotency-Key</c> within the owner. A
/// retry returns the stored outcome without repeating the change.
/// </summary>
public class ManualCommandReceipt
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string IdempotencyKey { get; set; }
    public required string Action { get; set; }

    /// <summary>SHA-256 of the canonical command input.</summary>
    public required string Fingerprint { get; set; }

    public int ResponseStatus { get; set; }

    /// <summary>The JSON response returned for the first, committed execution.</summary>
    public required string ResponseBody { get; set; }

    /// <summary>JSON object of the resources the command created or changed.</summary>
    public string ResourceIds { get; set; } = "{}";

    public required string Actor { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Stable text for who did something, stored on allocations, decisions, and changes.</summary>
public static class ActorLabels
{
    public const string System = "system";

    public static string Of(Guid? userId, Guid? apiKeyId, string? description) =>
        userId is { } user ? $"user:{user}"
        : apiKeyId is { } key ? $"api_key:{key}"
        : description is { Length: > 0 } text ? (text.Length <= 200 ? text : text[..200])
        : System;
}
