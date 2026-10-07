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
    public Guid BankConnectionId { get; set; }

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
}

/// <summary>
/// A bank movement described by accepted notifications. Recording it changes no payment request;
/// matching and allocations are a later feature, which must ignore anything not
/// <see cref="IngestionStatus.Accepted"/> and anything that is not live, received evidence.
/// </summary>
public class IncomingTransaction
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid BankConnectionId { get; set; }

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

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
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
    public Guid BankConnectionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
