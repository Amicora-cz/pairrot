using System.Text.Json;
using Pairrot.Core.Matching;
using Pairrot.Core.Payments;
using Pairrot.Core.Transactions;
using Pairrot.Infrastructure.Matching;

namespace Pairrot.Api.Http;

// Feature 009. Every write that changes a payment result returns the records it changed, so the
// caller sees the committed state without another request.

/// <summary>What matching says about a transaction.</summary>
/// <param name="Result">The latest automatic result, e.g. <c>matched</c>, <c>amount_mismatch</c>, <c>request_not_found</c>.</param>
/// <param name="PaymentRequestId">The request that result refers to: a suggestion, not a reservation.</param>
/// <param name="ManuallyConfirmed">A review confirmed the receipt (<c>manual_confirmation</c>); authentication evidence is unchanged.</param>
/// <param name="Allocatable">Whether a person can allocate it now.</param>
/// <param name="ManualBlock">Why no one can allocate it now.</param>
/// <param name="AutomaticBlock">Why automatic matching skips it.</param>
public sealed record TransactionMatchingResponse(
    string? Result,
    Guid? PaymentRequestId,
    DateTimeOffset? CheckedAt,
    bool AutoMatchingBlocked,
    string? AutoMatchingBlockReason,
    bool ManuallyConfirmed,
    string? HandlingReason,
    DateTimeOffset? HandledAt,
    string? HandledBy,
    Guid? CanonicalTransactionId,
    AllocationResponse? Allocation,
    IReadOnlyList<Guid> OpenReviewCaseIds,
    bool Allocatable,
    string? ManualBlock,
    string? AutomaticBlock)
{
    public static TransactionMatchingResponse From(IncomingTransaction t, TransactionMatching m) => new(
        t.MatchResult,
        t.MatchRequestId,
        t.MatchCheckedAt,
        t.AutoMatchBlocked,
        t.AutoMatchBlockReason,
        t.ManuallyConfirmed,
        t.HandlingReason,
        t.HandledAt,
        t.HandledBy,
        t.CanonicalTransactionId,
        m.Allocation is { } allocation ? AllocationResponse.From(allocation) : null,
        m.OpenReviewCaseIds,
        m.ManualBlock is null,
        m.ManualBlock,
        m.AutomaticBlock);
}

/// <param name="Method"><c>automatic</c> or <c>manual</c>.</param>
/// <param name="State"><c>active</c> or <c>revoked</c>; revoked allocations stay as history.</param>
/// <param name="Version">Send it as <c>expected_allocation_version</c> when revoking.</param>
/// <param name="BankConnectionId">The transaction's connection.</param>
/// <param name="PaymentRequestBankConnectionId">The request's connection: another registration of the same account is possible.</param>
public sealed record AllocationResponse(
    Guid Id,
    Guid TransactionId,
    Guid PaymentRequestId,
    Guid BankConnectionId,
    Guid PaymentRequestBankConnectionId,
    Guid BankAccountId,
    long Amount,
    string Currency,
    string Method,
    string? Reason,
    string Evidence,
    string State,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    string? RevokedBy,
    DateTimeOffset? RevokedAt,
    string? RevocationCode,
    string? RevocationReason,
    int Version)
{
    public static AllocationResponse From(PaymentAllocation a) => new(
        a.Id,
        a.TransactionId,
        a.PaymentRequestId,
        a.BankConnectionId,
        a.PaymentRequestBankConnectionId,
        a.BankAccountId,
        a.Amount,
        a.Currency,
        InboundMessageResponse.Snake(a.Method),
        a.Reason,
        a.Evidence,
        InboundMessageResponse.Snake(a.State),
        a.CreatedBy,
        a.CreatedAt,
        a.RevokedBy,
        a.RevokedAt,
        a.RevocationCode,
        a.RevocationReason,
        a.Version);
}

/// <summary>The result of allocating or revoking: the allocation and both records it changed.</summary>
public sealed record AllocationOutcomeResponse(
    AllocationResponse Allocation, IncomingTransactionResponse Transaction, PaymentRequestResponse PaymentRequest)
{
    public static AllocationOutcomeResponse From(AllocationOutcome outcome, DateTimeOffset now) => new(
        AllocationResponse.From(outcome.Allocation),
        IncomingTransactionResponse.From(outcome.Transaction),
        PaymentRequestResponse.From(outcome.Request, now));
}

public sealed record PaymentSummaryResponse(PaymentRequestStatus Status, long PaidAmount, long? RemainingAmount, long OverpaidAmount)
{
    public static PaymentSummaryResponse From(PaymentSummary s) => new(s.Status, s.PaidAmount, s.RemainingAmount, s.OverpaidAmount);
}

/// <param name="Proposed">The request's result if the transaction were allocated to it.</param>
/// <param name="Block">Why this allocation is not possible now, if it is not.</param>
public sealed record MatchingCandidateResponse(
    PaymentRequestResponse PaymentRequest, IReadOnlyList<string> Reasons, PaymentSummaryResponse? Proposed, string? Block);

public sealed record MatchingCandidatesResponse(IncomingTransactionResponse Transaction, IReadOnlyList<MatchingCandidateResponse> Candidates)
{
    public static MatchingCandidatesResponse From(CandidateList list, DateTimeOffset now) => new(
        IncomingTransactionResponse.From(list.Transaction),
        list.Candidates.Select(c => new MatchingCandidateResponse(
            PaymentRequestResponse.From(c.Request, now),
            c.Reasons,
            c.Proposed is { } proposed ? PaymentSummaryResponse.From(proposed) : null,
            c.Block)).ToList());
}

/// <summary>One immutable change of a request's payment result, in sequence order.</summary>
/// <param name="Cause"><c>automatic_allocation</c>, <c>manual_allocation</c>, <c>allocation_revoked</c>, <c>transaction_review_required</c>, or <c>cancelled</c>.</param>
public sealed record PaymentChangeResponse(
    Guid EventId,
    Guid PaymentRequestId,
    Guid TenantId,
    int Sequence,
    string ExternalId,
    long ExpectedAmount,
    string Currency,
    PaymentSummaryResponse Before,
    PaymentSummaryResponse After,
    string Cause,
    Guid? AllocationId,
    Guid? TransactionId,
    string? Evidence,
    string Actor,
    DateTimeOffset CreatedAt)
{
    public static PaymentChangeResponse From(PaymentChange c) => new(
        c.Id,
        c.PaymentRequestId,
        c.TenantId,
        c.Sequence,
        c.ExternalId,
        c.ExpectedAmount,
        c.Currency,
        new PaymentSummaryResponse(c.StatusBefore, c.PaidBefore, c.RemainingBefore, c.OverpaidBefore),
        new PaymentSummaryResponse(c.StatusAfter, c.PaidAfter, c.RemainingAfter, c.OverpaidAfter),
        InboundMessageResponse.Snake(c.Cause),
        c.AllocationId,
        c.TransactionId,
        c.Evidence,
        c.Actor,
        c.CreatedAt);
}

/// <param name="NextAfter">Pass as <c>after</c> for the next page; <c>null</c> on the last page.</param>
public sealed record PaymentChangeListResponse(IReadOnlyList<PaymentChangeResponse> Items, int? NextAfter);

/// <param name="Issue"><c>unverified_candidate</c>, <c>possible_duplicate</c>, <c>disputed</c>, or <c>account_movement_conflict</c> (decided by an operator).</param>
/// <param name="Version">Send it as <c>expected_version</c> when resolving or reopening.</param>
public sealed record ReviewCaseResponse(
    Guid Id,
    Guid TenantId,
    Guid BankConnectionId,
    Guid BankAccountId,
    string Issue,
    string State,
    Guid? InboundMessageId,
    string ReasonCode,
    string ReasonMessage,
    string? Resolution,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ResolvedAt,
    int Version)
{
    public static ReviewCaseResponse From(PaymentReviewCase c) => new(
        c.Id,
        c.TenantId,
        c.BankConnectionId,
        c.BankAccountId,
        InboundMessageResponse.Snake(c.Issue),
        InboundMessageResponse.Snake(c.State),
        c.InboundMessageId,
        c.ReasonCode,
        c.ReasonMessage,
        c.Resolution is { } resolution ? InboundMessageResponse.Snake(resolution) : null,
        c.CreatedAt,
        c.UpdatedAt,
        c.ResolvedAt,
        c.Version);
}

/// <param name="Members">The reviewed transactions with the versions the decision was made on.</param>
public sealed record ReviewDecisionResponse(
    Guid Id,
    string Action,
    string Reason,
    string? ReasonCode,
    string Actor,
    int CaseVersion,
    JsonElement Members,
    Guid? CanonicalTransactionId,
    Guid? CreatedTransactionId,
    DateTimeOffset CreatedAt)
{
    public static ReviewDecisionResponse From(PaymentReviewDecision d) => new(
        d.Id,
        InboundMessageResponse.Snake(d.Action),
        d.Reason,
        d.ReasonCode,
        d.Actor,
        d.CaseVersion,
        JsonDocument.Parse(d.Members).RootElement.Clone(),
        d.CanonicalTransactionId,
        d.CreatedTransactionId,
        d.CreatedAt);
}

/// <summary>
/// A case with its evidence. Parse runs keep the provider's authentication results as received;
/// a person's confirmation appears only as a decision, never as passing authentication.
/// </summary>
public sealed record ReviewCaseDetailResponse(
    ReviewCaseResponse Case,
    IReadOnlyList<IncomingTransactionResponse> Transactions,
    IReadOnlyList<ParseRunResponse> ParseRuns,
    IReadOnlyList<ReviewDecisionResponse> Decisions)
{
    public static ReviewCaseDetailResponse From(ReviewCaseDetail detail) => new(
        ReviewCaseResponse.From(detail.Case),
        detail.Transactions.Select(t => IncomingTransactionResponse.From(t)).ToList(),
        detail.Runs.Select(ParseRunResponse.From).ToList(),
        detail.Decisions.Select(ReviewDecisionResponse.From).ToList());
}
