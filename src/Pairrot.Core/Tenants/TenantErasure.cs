namespace Pairrot.Core.Tenants;

/// <summary>
/// Feature 014: the erasure of one tenant. Requesting it sets the tenant's collection barrier and
/// queues a background job that purges the tenant's personal data in batches; financial facts and
/// internal identifiers stay. One per tenant, never removed.
/// </summary>
public class TenantErasure
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public TenantErasureState State { get; set; } = TenantErasureState.Queued;

    public DateTimeOffset RequestedAt { get; set; }

    /// <summary>The technical actor (<c>api_key:…</c>, <c>user:…</c>, or the operator); never a name.</summary>
    public required string RequestedBy { get; set; }

    /// <summary>The requester's free-text reason; removed when the erasure completes.</summary>
    public string? Reason { get; set; }

    /// <summary>SHA-256 of the request's <c>Idempotency-Key</c>; the raw key is not kept.</summary>
    public string? IdempotencyKeySha256 { get; set; }

    /// <summary>The job running the erasure.</summary>
    public Guid JobId { get; set; }

    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>JSON object: records changed per record type, committed with each batch.</summary>
    public string Counts { get; set; } = "{}";

    /// <summary>
    /// JSON array of limitation codes the job reports, e.g. <c>delivered_copies_not_recalled</c>:
    /// what Pairrot cannot undo (requests already sent to receivers).
    /// </summary>
    public string Limitations { get; set; } = "[]";
}

public enum TenantErasureState
{
    Queued,
    Running,
    Done,
}

/// <summary>Placeholders and masks written by erasure (Feature 014).</summary>
public static class ErasureRules
{
    /// <summary>The placeholder for erased names and required free text.</summary>
    public const string Placeholder = "erased";

    /// <summary>Why queued webhook deliveries of an erased tenant were abandoned.</summary>
    public const string AbandonReason = "tenant_erased";

    public const string TombstonePrefix = "erased:";

    /// <summary>A unique replacement for a caller-supplied reference, e.g. <c>erased:0192…</c>.</summary>
    public static string Tombstone(Guid recordId) => $"{TombstonePrefix}{recordId}";

    public static bool IsTombstone(string? value) => value?.StartsWith(TombstonePrefix, StringComparison.Ordinal) == true;

    /// <summary>
    /// The database pattern of an IBAN masked by erasure, e.g. <c>CZ…0800…99</c>: the bank code and
    /// the last two digits tell two connections apart, but neither pay nor identify the account.
    /// Account numbers are masked the same way, e.g. <c>…99/0800</c>.
    /// </summary>
    public const string MaskedIbanPattern = "^CZ…[0-9]{4}…[0-9]{2}$";
}
