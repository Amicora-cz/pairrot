using Pairrot.Core.Inbound;
using Pairrot.Core.Parsing;

namespace Pairrot.Core.Transactions;

/// <summary>
/// One immutable attempt by a specific parser version to interpret a stored message. A new
/// generation is a new run; earlier runs are never changed.
/// </summary>
public class MessageParseRun
{
    public Guid Id { get; set; }
    public Guid InboundMessageId { get; set; }
    public Guid TenantId { get; set; }

    /// <summary>The receiving connection: the message's.</summary>
    public Guid BankConnectionId { get; set; }

    /// <summary>The account of the receiving connection; the transaction it names may belong to another connection of it.</summary>
    public Guid BankAccountId { get; set; }

    /// <summary>The parse job generation; unique per message.</summary>
    public int JobGeneration { get; set; }

    /// <summary>Digest of the source snapshot this run read.</summary>
    public required string SourceSha256 { get; set; }

    /// <summary><c>null</c> when no profile handles the bank.</summary>
    public string? BankProfile { get; set; }

    public string? ParserVersion { get; set; }
    public TrustDecision? TrustDecision { get; set; }

    /// <summary>JSON <see cref="Parsing.TrustEvidence"/> and its reasons.</summary>
    public string? TrustEvidence { get; set; }

    public ParseOutcome Outcome { get; set; }
    public required string ReasonCode { get; set; }
    public required string ReasonMessage { get; set; }

    /// <summary>JSON array of every <see cref="ParseReason"/>.</summary>
    public string Reasons { get; set; } = "[]";

    public string? TemplateId { get; set; }
    public SourcePart? SourcePart { get; set; }

    /// <summary>JSON <see cref="TransferCandidate"/>, including the raw extraction; a proposal, not a receipt.</summary>
    public string? Candidate { get; set; }

    /// <summary>The transaction this run created, linked to, or disputed.</summary>
    public Guid? TransactionId { get; set; }

    /// <summary>Who queued the generation: <c>system</c>, a user, an API key, or an operator.</summary>
    public required string RequestedBy { get; set; }

    /// <summary>The named worker that completed the run.</summary>
    public required string Worker { get; set; }

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset CompletedAt { get; set; }

    /// <summary>Feature 014: the candidate, trust evidence, and reason texts were purged (retention or erasure).</summary>
    public DateTimeOffset? PurgedAt { get; set; }
}

/// <summary>
/// A bank movement described by accepted notifications. Recording it changes no payment request;
/// Feature 009 matches it: automatically only when it is <see cref="IngestionStatus.Accepted"/>,
/// live, received evidence, and in no open review case; manually also after a review confirmed it.
/// </summary>
public class IncomingTransaction
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }

    /// <summary>The connection that received the establishing message.</summary>
    public Guid BankConnectionId { get; set; }

    /// <summary>The physical account (Feature 013); references and lookalikes are compared over it.</summary>
    public Guid BankAccountId { get; set; }

    /// <summary>Positive minor units.</summary>
    public long Amount { get; set; }

    public required string Currency { get; set; }

    /// <summary>As the bank printed it.</summary>
    public string? VariableSymbol { get; set; }

    /// <summary>Feature 005's comparison key; <c>null</c> without a symbol, never <c>0</c>.</summary>
    public string? VariableSymbolKey { get; set; }

    /// <summary>A reference the profile proved stable; never an email ID or a symbol.</summary>
    public string? BankTransactionId { get; set; }

    public string? SenderAccount { get; set; }
    public string? SenderAccountRaw { get; set; }
    public string? SenderName { get; set; }
    public string? Message { get; set; }
    public DateOnly BookedOn { get; set; }
    public DateTimeOffset? OccurredAt { get; set; }

    /// <summary>When the receiving system received the establishing message.</summary>
    public DateTimeOffset ReceivedAt { get; set; }

    public required string BankProfile { get; set; }
    public required string ParserVersion { get; set; }
    public IngestionStatus IngestionStatus { get; set; }

    /// <summary>JSON array of reasons (code, message, parse run, time) for review or dispute; history is appended, never replaced.</summary>
    public string IngestionReasons { get; set; } = "[]";

    /// <summary>A review hint for similar movements; not unique, not an identity.</summary>
    public required string ComparisonFingerprint { get; set; }

    /// <summary>Copied from the establishing message: fixture or test evidence never counts as a live bank receipt.</summary>
    public InboundSourceKind SourceKind { get; set; }

    public InboundEnvironment Environment { get; set; }

    /// <summary>How the establishing notification arrived (Feature 008), so a later policy can tell direct and forwarded receipts apart.</summary>
    public OriginPath OriginPath { get; set; }

    /// <summary>Feature 009: what has been decided about this money. Independent of the parser's <see cref="IngestionStatus"/>.</summary>
    public TransactionHandling Handling { get; set; } = TransactionHandling.Unallocated;

    /// <summary>Why it was ignored, rejected, or marked duplicate.</summary>
    public string? HandlingReason { get; set; }

    public DateTimeOffset? HandledAt { get; set; }
    public string? HandledBy { get; set; }

    /// <summary>Set by <see cref="TransactionHandling.Duplicate"/>: the record that represents the receipt.</summary>
    public Guid? CanonicalTransactionId { get; set; }

    /// <summary>A manual decision stops automatic matching until someone explicitly enables it again.</summary>
    public bool AutoMatchBlocked { get; set; }

    /// <summary>
    /// Feature 013: an earlier registration of the account by another tenant recorded a lookalike
    /// movement. Nobody can allocate this record until an operator confirms it is a distinct
    /// payment (<c>confirm_distinct</c> on its <c>account_movement_conflict</c> case).
    /// </summary>
    public bool MovementConflict { get; set; }

    public string? AutoMatchBlockReason { get; set; }

    /// <summary>
    /// A review decision confirmed this receipt (<c>manual_confirmation</c>): it may be allocated
    /// manually, never automatically. Authentication evidence is unchanged.
    /// </summary>
    public bool ManuallyConfirmed { get; set; }

    public Guid? ConfirmationDecisionId { get; set; }

    /// <summary>The latest automatic matching result, e.g. <c>matched</c> or <c>amount_mismatch</c>.</summary>
    public string? MatchResult { get; set; }

    /// <summary>The request the latest matching result refers to, if any (a suggestion, not a reservation).</summary>
    public Guid? MatchRequestId { get; set; }

    public DateTimeOffset? MatchCheckedAt { get; set; }

    /// <summary>Exposed for manual writes; incremented whenever eligibility or handling changes.</summary>
    public int Version { get; set; } = 1;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Feature 014: the sender fields and reason texts were purged (retention or erasure); the financial facts remain.</summary>
    public DateTimeOffset? PayerDataPurgedAt { get; set; }

    /// <summary>Feature 014: also the free-text handling reason was erased with the tenant.</summary>
    public DateTimeOffset? ErasedAt { get; set; }
}

public enum TransactionHandling
{
    /// <summary>Not counted towards any request (yet).</summary>
    Unallocated,

    /// <summary>Counted in full towards one request by an active allocation.</summary>
    Allocated,

    /// <summary>Unrelated to any request; excluded from matching until restored.</summary>
    Ignored,

    /// <summary>A review decided it is not a valid receipt.</summary>
    Rejected,

    /// <summary>A review decided it is another copy of <see cref="IncomingTransaction.CanonicalTransactionId"/>.</summary>
    Duplicate,
}

public enum IngestionStatus
{
    Accepted,

    /// <summary>Possibly the same movement as another record; not counted as money.</summary>
    ReviewRequired,

    /// <summary>Later evidence contradicts it; not counted as money.</summary>
    Disputed,
}

/// <summary>A message supporting a transaction. In the one-movement-per-message scope a message supports at most one.</summary>
public class TransactionSource
{
    public Guid Id { get; set; }
    public Guid TransactionId { get; set; }
    public Guid InboundMessageId { get; set; }

    /// <summary>The run that established the link.</summary>
    public Guid ParseRunId { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>The connection that received the message; may differ from the transaction's when both register one account.</summary>
    public Guid BankConnectionId { get; set; }

    public Guid BankAccountId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
