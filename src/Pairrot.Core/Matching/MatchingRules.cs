using Pairrot.Core.Inbound;
using Pairrot.Core.Payments;
using Pairrot.Core.Transactions;

namespace Pairrot.Core.Matching;

/// <summary>Stable codes explaining why a transaction was or was not matched automatically.</summary>
public static class MatchResults
{
    public const string Matched = "matched";
    public const string MissingVariableSymbol = "missing_variable_symbol";
    public const string RequestNotFound = "request_not_found";
    public const string AmountMismatch = "amount_mismatch";
    public const string CurrencyMismatch = "currency_mismatch";
    public const string RequestCancelled = "request_cancelled";

    /// <summary>The request already has a payment: a top-up or second receipt needs a person.</summary>
    public const string RequestAlreadyPaid = "request_already_paid";

    public const string MultipleCandidates = "multiple_candidates";
    public const string ReviewRequired = "review_required";

    /// <summary>A review confirmed the receipt; only a person allocates it.</summary>
    public const string ManualAllocationOnly = "manual_allocation_only";

    public const string AutoMatchingBlocked = "auto_matching_blocked";

    /// <summary>Feature 013: another tenant's registration of the account recorded a similar movement; an operator decides.</summary>
    public const string AccountMovementConflict = "account_movement_conflict";
    public const string NotLiveEvidence = "not_live_evidence";
    public const string TenantNotActive = "tenant_not_active";
    public const string BankConnectionArchived = "bank_connection_archived";
    public const string AlreadyAllocated = "already_allocated";
    public const string Ignored = "ignored";
    public const string Rejected = "rejected";
    public const string Duplicate = "duplicate";
}

/// <summary>Why an allocation was revoked; also the transaction's automatic-matching block reason.</summary>
public static class RevocationCodes
{
    /// <summary>A person revoked it, e.g. to reassign the receipt.</summary>
    public const string Manual = "manual";

    /// <summary>System correction: the transaction lost its eligibility.</summary>
    public const string TransactionReviewRequired = "transaction_review_required";

    /// <summary>Block reason after an ignored transaction is restored.</summary>
    public const string Restored = "restored";

    /// <summary>Block reason after a review confirmed the receipt (manual allocation only).</summary>
    public const string ManualConfirmation = "manual_confirmation";
}

/// <summary>
/// Feature 009's matching rules, free of persistence. Automatic matching is exact: one open
/// request of the same tenant and bank account (any of its registrations, Feature 013), with the
/// same currency, variable symbol key, and full amount. Nothing is
/// inferred from names, messages, similar amounts, or scores.
/// </summary>
public static class MatchingRules
{
    public const int MaxReasonLength = 500;

    /// <summary>Received, live evidence. Test and fixture records never pay live requests.</summary>
    public static bool IsLive(IncomingTransaction t) =>
        t.SourceKind == InboundSourceKind.Received && t.Environment == InboundEnvironment.Live;

    /// <summary>
    /// Why the transaction cannot be allocated by anyone right now, or <c>null</c>. A manually
    /// confirmed receipt passes here; <see cref="AutomaticBlock"/> adds the automatic-only rules.
    /// </summary>
    public static string? ManualBlock(IncomingTransaction t, bool inOpenReviewCase, bool allowTestEvidence) => t switch
    {
        { Handling: TransactionHandling.Allocated } => MatchResults.AlreadyAllocated,
        { Handling: TransactionHandling.Ignored } => MatchResults.Ignored,
        { Handling: TransactionHandling.Rejected } => MatchResults.Rejected,
        { Handling: TransactionHandling.Duplicate } => MatchResults.Duplicate,
        { MovementConflict: true } => MatchResults.AccountMovementConflict,
        _ when inOpenReviewCase => MatchResults.ReviewRequired,
        { IngestionStatus: not IngestionStatus.Accepted, ManuallyConfirmed: false } => MatchResults.ReviewRequired,
        _ when !allowTestEvidence && !IsLive(t) => MatchResults.NotLiveEvidence,
        _ => null,
    };

    public static string? AutomaticBlock(IncomingTransaction t, bool inOpenReviewCase, bool allowTestEvidence) =>
        ManualBlock(t, inOpenReviewCase, allowTestEvidence) ?? t switch
        {
            { ManuallyConfirmed: true } or { IngestionStatus: not IngestionStatus.Accepted } => MatchResults.ManualAllocationOnly,
            { AutoMatchBlocked: true } => MatchResults.AutoMatchingBlocked,
            _ => null,
        };

    /// <summary>
    /// The automatic decision for an eligible transaction among the requests of its tenant and
    /// account with the same symbol key (normally zero or one).
    /// </summary>
    public static string Decide(IncomingTransaction t, IReadOnlyList<PaymentRequest> sameSymbol)
    {
        if (t.VariableSymbolKey is null)
        {
            return MatchResults.MissingVariableSymbol;
        }

        var candidates = sameSymbol
            .Where(r => r.TenantId == t.TenantId && r.BankAccountId == t.BankAccountId && r.VariableSymbolKey == t.VariableSymbolKey)
            .ToList();
        return candidates switch
        {
            [] => MatchResults.RequestNotFound,
            [_, _, ..] => MatchResults.MultipleCandidates,
            [var r] when r.Currency != t.Currency => MatchResults.CurrencyMismatch,
            [{ Status: PaymentRequestStatus.Cancelled }] => MatchResults.RequestCancelled,
            [var r] when r.Status != PaymentRequestStatus.Open || r.PaidAmount != 0 => MatchResults.RequestAlreadyPaid,
            [var r] when r.Amount != t.Amount => MatchResults.AmountMismatch,
            _ => MatchResults.Matched,
        };
    }

    /// <summary>A reason a person must give: trimmed, nonblank, at most <see cref="MaxReasonLength"/> characters.</summary>
    public static string? ValidateReason(string? reason) => reason switch
    {
        null => "reason is required.",
        _ when string.IsNullOrWhiteSpace(reason) => "reason must not be blank.",
        _ when reason.Trim().Length > MaxReasonLength => $"reason must be at most {MaxReasonLength} characters.",
        _ => null,
    };
}
