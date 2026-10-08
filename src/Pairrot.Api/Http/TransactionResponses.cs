using System.Text.Json;
using Pairrot.Core.Inbound;
using Pairrot.Core.Jobs;
using Pairrot.Core.Transactions;
using Pairrot.Infrastructure.Matching;
using Pairrot.Infrastructure.Transactions;

namespace Pairrot.Api.Http;

/// <summary>
/// A recorded bank movement. <see cref="Evidence"/> is <c>live</c> only for messages received by
/// a live instance; fixture and test records are never live bank receipts. Recording a
/// transaction marks no payment request paid; an allocation does (Feature 009, see <see cref="Matching"/>).
/// </summary>
/// <param name="Evidence"><c>live</c>, <c>test</c>, or <c>fixture</c>.</param>
/// <param name="HandlingState"><c>unallocated</c>, <c>allocated</c>, <c>ignored</c>, <c>rejected</c>, or <c>duplicate</c>.</param>
/// <param name="Version">Send it as <c>expected_transaction_version</c> / <c>expected_version</c> in manual writes.</param>
public sealed record IncomingTransactionResponse(
    Guid Id,
    Guid TenantId,
    Guid BankConnectionId,
    Guid BankAccountId,
    long Amount,
    string Currency,
    string? VariableSymbol,
    string? BankTransactionId,
    string? SenderAccount,
    string? SenderName,
    string? Message,
    string BookedOn,
    DateTimeOffset? OccurredAt,
    DateTimeOffset ReceivedAt,
    string BankProfile,
    string ParserVersion,
    string IngestionStatus,
    IReadOnlyList<IngestionReason> IngestionReasons,
    string Evidence,
    string OriginPath,
    string HandlingState,
    int Version,
    TransactionMatchingResponse? Matching,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<TransactionSourceResponse>? Sources = null)
{
    public static IncomingTransactionResponse From(TransactionView view, IReadOnlyList<TransactionSource>? sources = null) =>
        From(view.Transaction, view.Matching, sources);

    public static IncomingTransactionResponse From(
        IncomingTransaction t, TransactionMatching? matching = null, IReadOnlyList<TransactionSource>? sources = null) => new(
        t.Id,
        t.TenantId,
        t.BankConnectionId,
        t.BankAccountId,
        t.Amount,
        t.Currency,
        t.VariableSymbol,
        t.BankTransactionId,
        t.SenderAccount ?? t.SenderAccountRaw,
        t.SenderName,
        t.Message,
        t.BookedOn.ToString("yyyy-MM-dd"),
        t.OccurredAt,
        t.ReceivedAt,
        t.BankProfile,
        t.ParserVersion,
        InboundMessageResponse.Snake(t.IngestionStatus),
        JsonSerializer.Deserialize<List<IngestionReason>>(t.IngestionReasons, StoredJson) ?? [],
        EvidenceOf(t.SourceKind, t.Environment),
        InboundMessageResponse.Snake(t.OriginPath),
        InboundMessageResponse.Snake(t.Handling),
        t.Version,
        matching is null ? null : TransactionMatchingResponse.From(t, matching),
        t.CreatedAt,
        t.UpdatedAt,
        sources?.Select(s => new TransactionSourceResponse(s.InboundMessageId, s.ParseRunId, s.BankConnectionId, s.CreatedAt)).ToList());

    public static string EvidenceOf(InboundSourceKind kind, InboundEnvironment environment) =>
        kind == InboundSourceKind.Fixture ? "fixture" : environment == InboundEnvironment.Live ? "live" : "test";

    internal static readonly JsonSerializerOptions StoredJson = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
}

/// <param name="InboundMessageId">Readable at <c>.../inbound-messages/{id}</c> under the same tenant route.</param>
/// <param name="BankConnectionId">The connection that received the message; another registration of the transaction's account is possible.</param>
public sealed record TransactionSourceResponse(Guid InboundMessageId, Guid ParseRunId, Guid BankConnectionId, DateTimeOffset LinkedAt);

/// <summary>
/// One parse attempt. <see cref="Candidate"/> is what the parser extracted; under review it is a
/// proposal, not a recorded transaction.
/// </summary>
public sealed record ParseRunResponse(
    Guid Id,
    Guid InboundMessageId,
    int Generation,
    string SourceSha256,
    string? BankProfile,
    string? ParserVersion,
    string? TrustDecision,
    JsonElement? Trust,
    string Outcome,
    string ReasonCode,
    string ReasonMessage,
    JsonElement Reasons,
    string? TemplateId,
    string? SourcePart,
    JsonElement? Candidate,
    Guid? TransactionId,
    string RequestedBy,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt)
{
    public static ParseRunResponse From(MessageParseRun r) => new(
        r.Id,
        r.InboundMessageId,
        r.JobGeneration,
        r.SourceSha256,
        r.BankProfile,
        r.ParserVersion,
        r.TrustDecision is { } trust ? InboundMessageResponse.Snake(trust) : null,
        Parse(r.TrustEvidence),
        InboundMessageResponse.Snake(r.Outcome),
        r.ReasonCode,
        r.ReasonMessage,
        Parse(r.Reasons)!.Value,
        r.TemplateId,
        r.SourcePart is { } part ? InboundMessageResponse.Snake(part) : null,
        Parse(r.Candidate),
        r.TransactionId,
        r.RequestedBy,
        r.StartedAt,
        r.CompletedAt);

    private static JsonElement? Parse(string? json) => json is null ? null : JsonDocument.Parse(json).RootElement.Clone();
}

/// <param name="Jobs">Parse generations, oldest first; queue and retry state lives here, not in runs.</param>
public sealed record ParseHistoryResponse(
    Guid InboundMessageId,
    string ContentState,
    string? LatestOutcome,
    IReadOnlyList<ParseRunResponse> Runs,
    IReadOnlyList<RetrievalJobResponse> Jobs)
{
    public static ParseHistoryResponse From(ParseHistory history) => new(
        history.Message.Id,
        InboundMessageResponse.Snake(history.Message.ContentState),
        history.Message.LatestParseOutcome is { } outcome ? InboundMessageResponse.Snake(outcome) : null,
        history.Runs.Select(ParseRunResponse.From).ToList(),
        history.Jobs.Select(RetrievalJobResponse.From).ToList());
}

public sealed record QueuedParseResponse(Guid JobId, int Generation, string State, bool Created)
{
    public static QueuedParseResponse From(BackgroundJob job, bool created) =>
        new(job.Id, job.Generation, InboundMessageResponse.Snake(job.State), created);
}
