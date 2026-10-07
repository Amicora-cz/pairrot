namespace Pairrot.Core.Payments;

/// <summary>
/// An expected payment: which tenant expects money, on which bank account, for which reference in
/// the calling application. The destination and payment fields are frozen at creation, together
/// with the SPAYD payload issued for them; only the description and due time can change later.
/// </summary>
public class PaymentRequest
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid BankConnectionId { get; set; }

    /// <summary>The caller's reference (e.g. a NaLekci reservation), unique within the tenant.</summary>
    public required string ExternalId { get; set; }

    /// <summary>Snapshot of the connection's domestic account number at creation.</summary>
    public required string AccountNumber { get; set; }

    /// <summary>Snapshot of the connection's IBAN at creation; the payload pays this account.</summary>
    public required string Iban { get; set; }

    /// <summary>Minor units (haléře).</summary>
    public long Amount { get; set; }

    public required string Currency { get; set; }

    /// <summary>The symbol as supplied, leading zeros kept; used for display and in the payload.</summary>
    public required string VariableSymbol { get; set; }

    /// <summary>The symbol without leading zeros; unique per bank connection. See <see cref="PaymentRequestRules.VariableSymbolKey"/>.</summary>
    public required string VariableSymbolKey { get; set; }

    public string? Message { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset? DueAt { get; set; }
    public PaymentRequestStatus Status { get; set; } = PaymentRequestStatus.Open;
    public string? CancellationReason { get; set; }

    /// <summary>SHA-256 of the original validated creation input; compared on creation retries, never updated.</summary>
    public required string CreationFingerprint { get; set; }

    public required string QrPayload { get; set; }
    public required string QrFormat { get; set; }
    public required string QrGeneratorVersion { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }

    /// <summary>Starts at 1 and is incremented on every change; exposed as the ETag.</summary>
    public int Version { get; set; } = 1;
}

/// <summary>
/// Stored states. Paid states come with allocations in a later feature; overdue is computed,
/// never stored (<see cref="PaymentRequestRules.IsOverdue"/>).
/// </summary>
public enum PaymentRequestStatus
{
    Open,
    Cancelled,
}
