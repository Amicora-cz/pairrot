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

    /// <summary>
    /// Sum of active allocations in minor units (Feature 009). Derived from allocations and updated
    /// in the same transaction as them; no API sets it directly.
    /// </summary>
    public long PaidAmount { get; set; }

    /// <summary>The sequence of the latest payment change (<c>payment_request_changes</c>); 0 before any.</summary>
    public int ChangeSequence { get; set; }

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
/// Stored states. Apart from <see cref="Cancelled"/>, the status follows from the paid amount
/// (<see cref="PaymentSummary.StatusFor"/>); overdue is computed, never stored
/// (<see cref="PaymentRequestRules.IsOverdue"/>).
/// </summary>
public enum PaymentRequestStatus
{
    Open,
    PartiallyPaid,
    Paid,
    Overpaid,
    Cancelled,
}

/// <summary>
/// A request's payment result. For a cancelled request nothing is paid and nothing remains.
/// </summary>
/// <param name="RemainingAmount"><c>null</c> for a cancelled request.</param>
public readonly record struct PaymentSummary(PaymentRequestStatus Status, long PaidAmount, long? RemainingAmount, long OverpaidAmount)
{
    public static PaymentSummary Of(PaymentRequest request) => request.Status == PaymentRequestStatus.Cancelled
        ? new PaymentSummary(PaymentRequestStatus.Cancelled, 0, null, 0)
        : Calculate(request.Amount, request.PaidAmount);

    public static PaymentSummary Calculate(long amount, long paid) =>
        new(StatusFor(amount, paid), paid, Math.Max(amount - paid, 0), Math.Max(paid - amount, 0));

    public static PaymentRequestStatus StatusFor(long amount, long paid) => paid switch
    {
        0 => PaymentRequestStatus.Open,
        _ when paid < amount => PaymentRequestStatus.PartiallyPaid,
        _ when paid == amount => PaymentRequestStatus.Paid,
        _ => PaymentRequestStatus.Overpaid,
    };

    /// <summary>The paid amount after adding <paramref name="delta"/>, or <c>null</c> when it would overflow or go negative.</summary>
    public static long? Add(long paid, long delta)
    {
        try
        {
            var result = checked(paid + delta);
            return result < 0 ? null : result;
        }
        catch (OverflowException)
        {
            return null;
        }
    }
}
