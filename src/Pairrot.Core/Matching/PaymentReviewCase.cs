namespace Pairrot.Core.Matching;

/// <summary>
/// An unresolved question about source evidence: a parser candidate that could not be trusted, a
/// possible duplicate, or a disputed transaction. Separate from ordinary matching failures such as
/// a missing variable symbol. While a case is open, none of its transactions can be allocated.
/// </summary>
public class PaymentReviewCase
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid BankConnectionId { get; set; }
    public ReviewIssue Issue { get; set; }
    public ReviewCaseState State { get; set; } = ReviewCaseState.Open;

    /// <summary>For <see cref="ReviewIssue.UnverifiedCandidate"/>: the message whose runs are reviewed.</summary>
    public Guid? InboundMessageId { get; set; }

    /// <summary>The reason code that opened the case, e.g. <c>possible_duplicate</c> or <c>untrusted_source</c>.</summary>
    public required string ReasonCode { get; set; }

    public required string ReasonMessage { get; set; }

    /// <summary>
    /// What the reviewed evidence says, so reprocessing unchanged evidence does not reopen a
    /// decided case: the reason code with the candidate's critical facts.
    /// </summary>
    public required string EvidenceKey { get; set; }

    /// <summary>The latest decision, e.g. <c>confirm_payment</c>; <c>null</c> while never resolved.</summary>
    public ReviewAction? Resolution { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }

    /// <summary>Incremented when members, evidence, or state change.</summary>
    public int Version { get; set; } = 1;
}

/// <summary>A transaction or parse run under review. Exactly one of the two is set.</summary>
public class PaymentReviewCaseMember
{
    public Guid Id { get; set; }
    public Guid CaseId { get; set; }
    public Guid TenantId { get; set; }
    public Guid BankConnectionId { get; set; }
    public Guid? TransactionId { get; set; }
    public Guid? ParseRunId { get; set; }
    public DateTimeOffset AddedAt { get; set; }
}

/// <summary>An immutable decision about a case, including reopening. Never edited or deleted.</summary>
public class PaymentReviewDecision
{
    public Guid Id { get; set; }
    public Guid CaseId { get; set; }
    public Guid TenantId { get; set; }
    public ReviewAction Action { get; set; }

    /// <summary>The check the user performed, e.g. "Checked the account statement in George".</summary>
    public required string Reason { get; set; }

    public required string Actor { get; set; }

    /// <summary>The case version the decision was made on.</summary>
    public int CaseVersion { get; set; }

    /// <summary>JSON array of the reviewed transactions with their versions, and parse runs.</summary>
    public string Members { get; set; } = "[]";

    public Guid? CanonicalTransactionId { get; set; }

    /// <summary>The transaction <see cref="ReviewAction.ConfirmPayment"/> created from a candidate.</summary>
    public Guid? CreatedTransactionId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public enum ReviewIssue
{
    /// <summary>The parser extracted a payment but could not accept it (e.g. unverified sender).</summary>
    UnverifiedCandidate,

    /// <summary>Records that may describe the same movement.</summary>
    PossibleDuplicate,

    /// <summary>Later evidence contradicts a recorded transaction.</summary>
    Disputed,
}

public enum ReviewCaseState
{
    Open,
    Resolved,
}

public enum ReviewAction
{
    ConfirmPayment,
    ConfirmDistinct,
    MarkDuplicate,
    KeepExisting,
    Reject,
    Reopen,

    /// <summary>System: the message later produced an accepted transaction itself.</summary>
    Superseded,
}
