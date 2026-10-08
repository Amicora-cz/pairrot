using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pairrot.Core.Banking;
using Pairrot.Core.Inbound;
using Pairrot.Core.Jobs;
using Pairrot.Core.Parsing;
using Pairrot.Core.Tenants;
using Pairrot.Core.Transactions;
using Pairrot.Infrastructure.Erasure;
using Pairrot.Infrastructure.Inbound;
using Pairrot.Infrastructure.Jobs;
using Pairrot.Infrastructure.Matching;
using Pairrot.Infrastructure.Persistence;
using Pairrot.Infrastructure.Tenants;

namespace Pairrot.Infrastructure.Transactions;

/// <summary>
/// Parses one stored message with the installed profile of its connection's bank. The run, any
/// new transaction or source link, review or dispute marks, the message projection, and the
/// job's completion are saved in one transaction, only while the job's lease is held, and
/// serialized per physical bank account (Feature 013), so concurrent messages, also received
/// through different registrations of the account, cannot both create the same movement. A lost
/// race on the account-wide bank reference index rolls the whole attempt back and evaluates
/// again, so the conflict is then recorded as a review. No provider is called: parsing reads the
/// saved snapshot only.
/// </summary>
public class MessageParseJob(
    PairrotDbContext db,
    BackgroundJobQueue jobs,
    BankProfileRegistry profiles,
    IOptions<ForwardingOptions> forwarding,
    ReviewCaseRecorder reviews,
    IOptions<RetentionOptions> retention,
    ILogger<MessageParseJob> logger) : IBackgroundJobHandler
{
    private const int SaveAttempts = 3;

    public string Type => BackgroundJobTypes.ParseInboundMessage;

    /// <summary>Parsing needs only the database, so every worker can run it.</summary>
    public bool IsEnabled => true;

    public async Task HandleAsync(JobLease lease, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await RunAsync(lease, ct);
                return;
            }
            catch (DbUpdateException ex) when (attempt < SaveAttempts && IsRace(ex))
            {
                // Another run changed the same message or transaction first; evaluate again on fresh data.
                db.ChangeTracker.Clear();
            }
        }
    }

    private async Task RunAsync(JobLease lease, CancellationToken ct)
    {
        var started = jobs.Now();
        var message = await db.InboundMessages.AsNoTracking().SingleOrDefaultAsync(m => m.Id == lease.TargetId, ct);
        if (message is null)
        {
            await FinishAsync(lease, job => jobs.Fail(job, "message_not_found", "The inbound message does not exist."), ct);
            return;
        }

        if (await ParseEligibility.HeldAsync(db, message, ct) is { } held)
        {
            // No run: nothing was parsed. Once the hold is resolved, someone queues the message again.
            await FinishAsync(lease, job => jobs.Fail(job, held.Code, held.Message), ct);
            return;
        }

        var connection = await db.BankConnections.AsNoTracking().SingleAsync(b => b.Id == message.BankConnectionId, ct);
        var account = CzechBankAccount.ParseIban(connection.Iban).Account
            ?? throw new InvalidOperationException($"Bank connection {connection.Id} has an invalid IBAN.");
        var profile = profiles.For(account);

        var sources = (await db.ForwardingSources.AsNoTracking()
                .Where(s => s.BankConnectionId == connection.Id && s.State == ForwardingSourceState.Active && s.Address != null)
                .Select(s => s.Address!)
                .ToListAsync(ct))
            .ToHashSet();
        ParseEvaluation evaluation;
        try
        {
            evaluation = BankMessageEvaluator.Evaluate(profile, ToBankMessage(message, account, connection.Currency) with
            {
                ForwardingSources = sources,
                SpfVerifiedForwarderDomains = forwarding.Value.SpfVerifiedForwarderDomainSet,
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Parsing message {MessageId} failed.", message.Id);
            await FailedAttemptAsync(lease, message, connection.BankAccountId, profile, started, $"Unexpected {ex.GetType().Name} while parsing the message.", ct);
            return;
        }

        await FinishAsync(lease, async job =>
        {
            if (await GoneAsync(message, job, ct))
            {
                return;
            }

            await PaymentLedger.LockAsync(db, connection.BankAccountId, ct);
            var run = NewRun(message, connection.BankAccountId, lease, job, profile, started);
            ApplyEvaluation(run, evaluation);
            db.MessageParseRuns.Add(run);
            if (profile is not null)
            {
                await RecordTransactionAsync(run, evaluation, message, profile, ct);
            }

            await ProjectAsync(message.Id, run, ct);
            jobs.Succeed(job);
        }, ct);
    }

    /// <summary>The common input: the stored snapshot and the routed connection, never provider data.</summary>
    public static BankMessage ToBankMessage(InboundMessage message, CzechBankAccount account, string currency) => new(
        message.HeaderFrom,
        message.Subject,
        message.TextBody,
        message.HtmlBody,
        new SenderAuthentication(message.SpfResult, message.DkimResult, message.DmarcResult, message.AuthenticationOrigin, null),
        message.SourceKind,
        message.Environment,
        message.ReceivedAt,
        account,
        currency)
    {
        EnvelopeSender = message.EnvelopeSender,
    };

    /// <summary>
    /// Duplicate and reprocessing rules: one movement per message; a proven bank reference links
    /// messages to one movement; changed facts dispute instead of overwriting; similar movements
    /// without proof are flagged, never merged. Feature 009: every unresolved result gets a review
    /// case, a transaction that loses eligibility loses its allocation in this same transaction,
    /// and a new accepted transaction is queued for matching. Feature 013: references and
    /// lookalikes are compared over the whole account. Within the tenant they link or review
    /// across its registrations; another tenant's records are never changed, linked, or shown,
    /// and the conflict is reviewed locally by an operator.
    /// </summary>
    private async Task RecordTransactionAsync(
        MessageParseRun run, ParseEvaluation evaluation, InboundMessage message, IBankProfile profile, CancellationToken ct)
    {
        var link = await db.TransactionSources.AsNoTracking().SingleOrDefaultAsync(s => s.InboundMessageId == message.Id, ct);
        var linked = link is null ? null : await db.IncomingTransactions.SingleAsync(t => t.Id == link.TransactionId, ct);

        if (evaluation.Outcome != ParseOutcome.Parsed || evaluation.Candidate is not { } candidate)
        {
            if (linked is not null)
            {
                run.TransactionId = linked.Id;
                if (linked.ManuallyConfirmed && evaluation.Candidate?.CriticalFacts == Facts(linked))
                {
                    // A person confirmed this receipt from the same unaccepted evidence; the decision stands.
                    Resolve(run, run.Outcome, ParseReasons.Unchanged,
                        "The message still supports the manually confirmed transaction; the review decision stands.");
                    return;
                }

                // A message that once supported a movement no longer does (untrusted, not a payment, unrecognized).
                var rejected = $"Parser {run.ParserVersion} no longer accepts the supporting message ({Snake(evaluation.Outcome)}).";
                Dispute(linked, run, ParseReasons.ReprocessingRejected, rejected);
                await reviews.OnDisputedAsync(linked, run, ParseReasons.ReprocessingRejected, rejected,
                    $"{Snake(evaluation.Outcome)}|{evaluation.Candidate?.CriticalFacts}", ct);
            }
            else if (evaluation.Outcome == ParseOutcome.ReviewRequired && evaluation.Candidate is { } proposal)
            {
                await reviews.OnUnverifiedCandidateAsync(message.Id, message.TenantId!.Value, message.BankConnectionId!.Value, run, proposal, ct);
            }

            return;
        }

        if (linked is not null)
        {
            run.TransactionId = linked.Id;
            if (Facts(linked) == candidate.CriticalFacts)
            {
                Resolve(run, ParseOutcome.Parsed, ParseReasons.Unchanged, "The message still supports the same transaction.");
            }
            else
            {
                var changed = $"Parser {run.ParserVersion} extracted different payment facts from the supporting message.";
                Dispute(linked, run, ParseReasons.ReprocessingChangedFacts, changed);
                Resolve(run, ParseOutcome.ReviewRequired, ParseReasons.ReprocessingChangedFacts,
                    "The new extraction changes payment-critical facts; the transaction is disputed and the proposal kept in this run.");
                await reviews.OnDisputedAsync(linked, run, ParseReasons.ReprocessingChangedFacts, changed, candidate.CriticalFacts, ct);
            }

            return;
        }

        if (candidate.BankReference is { } reference)
        {
            var known = await db.IncomingTransactions.SingleOrDefaultAsync(t =>
                t.BankAccountId == run.BankAccountId && t.BankProfile == profile.Name && t.BankTransactionId == reference, ct);
            if (known is not null && known.TenantId != run.TenantId)
            {
                // An earlier registration of the account by another tenant owns the movement. No
                // transaction, link, or change of its records; the review holds local evidence only.
                Resolve(run, ParseOutcome.ReviewRequired, ParseReasons.AccountMovementConflict,
                    "The bank reference is already recorded for this account by an earlier registration; no transaction was created.");
                await reviews.OnAccountMovementConflictAsync(run, null, ParseReasons.BankReferenceConflict,
                    ReviewCaseRecorder.ForeignReferenceMessage, candidate.CriticalFacts, ct);
                return;
            }

            if (known is not null)
            {
                run.TransactionId = known.Id;
                if (Facts(known) == candidate.CriticalFacts)
                {
                    AddSource(known, message, run);
                    Resolve(run, ParseOutcome.Duplicate, ParseReasons.Duplicate,
                        "Another notification of an already recorded movement (same bank reference).");
                    await reviews.SupersedeCandidateAsync(message.Id, ct);
                }
                else
                {
                    var conflict = "Another notification with the same bank reference states different payment facts.";
                    Dispute(known, run, ParseReasons.BankReferenceConflict, conflict);
                    Resolve(run, ParseOutcome.ReviewRequired, ParseReasons.BankReferenceConflict,
                        "The bank reference is already recorded with different facts; no second movement was created.");
                    await reviews.OnDisputedAsync(known, run, ParseReasons.BankReferenceConflict, conflict, candidate.CriticalFacts, ct);
                }

                return;
            }
        }

        var now = jobs.Now();
        var fingerprint = BankMessageEvaluator.ComparisonFingerprint(profile.Name, run.BankAccountId, candidate);

        // Different proven references are different movements even with equal amounts and symbols.
        // Lookalikes are searched over the whole account; only the tenant's own join its review.
        var lookalikes = (await db.IncomingTransactions
                .Where(t => t.BankAccountId == run.BankAccountId && t.ComparisonFingerprint == fingerprint)
                .ToListAsync(ct))
            .Where(t => t.BankTransactionId is null || candidate.BankReference is null)
            .ToList();
        var similar = lookalikes.Where(t => t.TenantId == run.TenantId).ToList();
        var foreign = lookalikes.Any(t => t.TenantId != run.TenantId);

        var transaction = new IncomingTransaction
        {
            Id = Guid.CreateVersion7(),
            TenantId = message.TenantId!.Value,
            BankConnectionId = message.BankConnectionId!.Value,
            BankAccountId = run.BankAccountId,
            Amount = candidate.Amount!.Value,
            Currency = candidate.Currency!,
            VariableSymbol = candidate.VariableSymbol,
            VariableSymbolKey = candidate.VariableSymbolKey,
            BankTransactionId = candidate.BankReference,
            SenderAccount = candidate.SenderAccount,
            SenderAccountRaw = BankMessageEvaluator.Limit(candidate.Raw.SenderAccount, 100),
            SenderName = candidate.SenderName,
            Message = candidate.Message,
            BookedOn = candidate.BookedOn!.Value,
            OccurredAt = candidate.OccurredAt?.ToUniversalTime(),
            ReceivedAt = message.ReceivedAt,
            BankProfile = profile.Name,
            ParserVersion = profile.Version,
            IngestionStatus = IngestionStatus.Accepted,
            ComparisonFingerprint = fingerprint,
            SourceKind = message.SourceKind,
            Environment = message.Environment,
            OriginPath = evaluation.Trust?.Evidence.OriginPath ?? OriginPath.Direct,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.IncomingTransactions.Add(transaction);
        AddSource(transaction, message, run);
        run.TransactionId = transaction.Id;
        await reviews.SupersedeCandidateAsync(message.Id, ct);

        if (foreign)
        {
            // Another tenant's registration recorded a lookalike: this record stays unallocatable
            // until an operator confirms it is distinct. The other record is not touched or shown.
            transaction.IngestionStatus = IngestionStatus.ReviewRequired;
            transaction.MovementConflict = true;
            transaction.AutoMatchBlocked = true;
            transaction.AutoMatchBlockReason = Core.Matching.MatchResults.AccountMovementConflict;
            AppendReason(transaction, run, ParseReasons.AccountMovementConflict, ReviewCaseRecorder.ForeignLookalikeMessage);
            Resolve(run, ParseOutcome.Parsed, ParseReasons.AccountMovementConflict, $"Recorded for operator review. {ReviewCaseRecorder.ForeignLookalikeMessage}");
            await reviews.OnAccountMovementConflictAsync(run, transaction, ParseReasons.PossibleDuplicate,
                ReviewCaseRecorder.ForeignLookalikeMessage, fingerprint, ct);
        }

        if (similar.Count > 0)
        {
            var hint = $"It resembles {similar.Count} other transaction(s) with the same amount, date, and symbol; it may be the same movement.";
            MarkForReview(transaction, run, hint);
            foreach (var other in similar.Where(t => t.IngestionStatus == IngestionStatus.Accepted))
            {
                MarkForReview(other, run, "A later notification resembles this transaction; it may be the same movement.");
            }

            Resolve(run, ParseOutcome.Parsed, ParseReasons.PossibleDuplicate, $"Recorded for review. {hint}");
            await reviews.OnPossibleDuplicateAsync(transaction, similar, run, ct);
        }
        else if (!foreign)
        {
            // Saved with the transaction, so a recorded receipt is always matched eventually.
            jobs.Add(BackgroundJobTypes.MatchTransaction, transaction.Id, 1, BackgroundJobQueue.SystemRequester);
        }
    }

    /// <summary>Links the message to the transaction; the link keeps the receiving connection, which may be another registration of the account.</summary>
    private void AddSource(IncomingTransaction transaction, InboundMessage message, MessageParseRun run) =>
        db.TransactionSources.Add(new TransactionSource
        {
            Id = Guid.CreateVersion7(),
            TransactionId = transaction.Id,
            InboundMessageId = message.Id,
            ParseRunId = run.Id,
            TenantId = transaction.TenantId,
            BankConnectionId = run.BankConnectionId,
            BankAccountId = run.BankAccountId,
            CreatedAt = jobs.Now(),
        });

    private void Dispute(IncomingTransaction transaction, MessageParseRun run, string code, string message)
    {
        transaction.IngestionStatus = IngestionStatus.Disputed;
        AppendReason(transaction, run, code, message);
    }

    private void MarkForReview(IncomingTransaction transaction, MessageParseRun run, string message)
    {
        if (transaction.IngestionStatus == IngestionStatus.Accepted)
        {
            transaction.IngestionStatus = IngestionStatus.ReviewRequired;
        }

        AppendReason(transaction, run, ParseReasons.PossibleDuplicate, message);
    }

    private void AppendReason(IncomingTransaction transaction, MessageParseRun run, string code, string message)
    {
        var reasons = JsonSerializer.Deserialize<List<IngestionReason>>(transaction.IngestionReasons, Json) ?? [];
        reasons.Add(new IngestionReason(code, message, run.Id, jobs.Now()));
        transaction.IngestionReasons = JsonSerializer.Serialize(reasons, Json);
        transaction.Version++;
        transaction.UpdatedAt = jobs.Now();
    }

    private static string Facts(IncomingTransaction t) =>
        TransferCandidate.Facts(t.Amount, t.Currency, t.VariableSymbol, t.BookedOn, t.BankTransactionId);

    private MessageParseRun NewRun(
        InboundMessage message, Guid accountId, JobLease lease, BackgroundJob job, IBankProfile? profile, DateTimeOffset started) => new()
    {
        Id = Guid.CreateVersion7(),
        InboundMessageId = message.Id,
        TenantId = message.TenantId!.Value,
        BankConnectionId = message.BankConnectionId!.Value,
        BankAccountId = accountId,
        JobGeneration = lease.Generation,
        SourceSha256 = message.ContentSha256!,
        BankProfile = profile?.Name,
        ParserVersion = profile?.Version,
        ReasonCode = ParseReasons.ProcessingFailed,
        ReasonMessage = "",
        RequestedBy = job.RequestedBy,
        Worker = lease.Owner,
        StartedAt = started,
        CompletedAt = jobs.Now(),
    };

    private static void ApplyEvaluation(MessageParseRun run, ParseEvaluation evaluation)
    {
        var primary = evaluation.PrimaryReason;
        SetResult(run, evaluation.Outcome, primary.Code, primary.Message);
        run.Reasons = JsonSerializer.Serialize(evaluation.Reasons, Json);
        run.TemplateId = evaluation.TemplateId;
        run.SourcePart = evaluation.Part;
        run.TrustDecision = evaluation.Trust?.Decision;
        run.TrustEvidence = evaluation.Trust is { } trust ? JsonSerializer.Serialize(trust, Json) : null;
        run.Candidate = evaluation.Candidate is { } candidate ? JsonSerializer.Serialize(candidate, Json) : null;
    }

    /// <summary>A result decided against stored transactions; it leads the run's reasons.</summary>
    private static void Resolve(MessageParseRun run, ParseOutcome outcome, string code, string message)
    {
        SetResult(run, outcome, code, message);
        var reasons = JsonSerializer.Deserialize<List<ParseReason>>(run.Reasons, Json) ?? [];
        reasons.Insert(0, new ParseReason(code, run.ReasonMessage));
        run.Reasons = JsonSerializer.Serialize(reasons, Json);
    }

    private static void SetResult(MessageParseRun run, ParseOutcome outcome, string code, string message)
    {
        run.Outcome = outcome;
        run.ReasonCode = code;
        run.ReasonMessage = BackgroundJobQueue.Truncate(message);
    }

    private async Task ProjectAsync(Guid messageId, MessageParseRun run, CancellationToken ct)
    {
        var tracked = await db.InboundMessages.SingleAsync(m => m.Id == messageId, ct);
        tracked.LatestParseRunId = run.Id;
        tracked.LatestParseOutcome = run.Outcome;
        tracked.ParsedAt = run.CompletedAt;
        tracked.UpdatedAt = run.CompletedAt;
    }

    /// <summary>Retries an unexpected failure; the last attempt records a <c>failed</c> run.</summary>
    private Task FailedAttemptAsync(
        JobLease lease, InboundMessage message, Guid accountId, IBankProfile? profile, DateTimeOffset started, string error, CancellationToken ct)
    {
        if (jobs.Schedule.NextAttempt(lease.AttemptCount, jobs.Now()) is { } next)
        {
            return FinishAsync(lease, job => BackgroundJobQueue.Retry(job, next, ParseReasons.ProcessingFailed, error), ct);
        }

        return FinishAsync(lease, async job =>
        {
            if (await GoneAsync(message, job, ct))
            {
                return;
            }

            var run = NewRun(message, accountId, lease, job, profile, started);
            SetResult(run, ParseOutcome.Failed, ParseReasons.ProcessingFailed, $"{error} No attempts left.");
            run.Reasons = JsonSerializer.Serialize(new[] { new ParseReason(run.ReasonCode, run.ReasonMessage) }, Json);
            db.MessageParseRuns.Add(run);
            await ProjectAsync(message.Id, run, ct);
            jobs.Fail(job, ParseReasons.ProcessingFailed, error);
        }, ct);
    }

    /// <summary>
    /// Feature 014: rechecked in the job's transaction before a run is saved. The message row lock
    /// orders this against a retention pass, the barrier lock against an erasure request; when the
    /// content was purged or the owner erased meanwhile, the result is discarded, not rescheduled.
    /// </summary>
    private async Task<bool> GoneAsync(InboundMessage read, BackgroundJob job, CancellationToken ct)
    {
        var current = (await db.InboundMessages.FromSql($"SELECT *, xmin FROM inbound_messages WHERE id = {read.Id} FOR UPDATE")
            .AsNoTracking().ToListAsync(ct)).Single();
        var erased = current.ContentState == InboundContentState.Erased || await DataBarrier.IsClosedAsync(db, read.TenantId!.Value, ct);
        var expired = !erased && (current.ContentState != InboundContentState.Available || current.ContentSha256 != read.ContentSha256
            || retention.Value.Enabled && current.ReceivedAt < retention.Value.CutoffAt(jobs.Now()));
        if (erased)
        {
            jobs.Fail(job, "content_erased", "The message's owner was erased; the result was discarded.");
        }
        else if (expired)
        {
            jobs.Fail(job, current.ContentState == InboundContentState.Available ? "content_changed" : "content_expired",
                "The message's content is no longer the one parsed; the result was discarded.");
        }

        return erased || expired;
    }

    private Task FinishAsync(JobLease lease, Action<BackgroundJob> apply, CancellationToken ct) =>
        FinishAsync(lease, job =>
        {
            apply(job);
            return Task.CompletedTask;
        }, ct);

    private async Task FinishAsync(JobLease lease, Func<BackgroundJob, Task> apply, CancellationToken ct)
    {
        if (!await jobs.FinishAsync(lease, apply, ct))
        {
            logger.LogWarning("Job {JobId} lost its lease before finishing; its result was discarded.", lease.JobId);
        }
    }

    private static bool IsRace(DbUpdateException ex) => ex is DbUpdateConcurrencyException
        || TenantService.IsUniqueViolation(ex, MessageParseRunConfiguration.GenerationUniqueIndex)
        || TenantService.IsUniqueViolation(ex, IncomingTransactionConfiguration.BankReferenceUniqueIndex)
        || TenantService.IsUniqueViolation(ex, TransactionSourceConfiguration.MessageUniqueIndex)
        || TenantService.IsUniqueViolation(ex, PaymentAllocationConfiguration.OneActiveIndex)
        || TenantService.IsUniqueViolation(ex, PaymentChangeConfiguration.SequenceUniqueIndex);

    internal static string Snake<TEnum>(TEnum value) where TEnum : struct, Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    /// <summary>Stored JSON: snake_case names and enum values, like the API.</summary>
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };
}

/// <param name="ParseRunId">The run that raised the reason.</param>
public sealed record IngestionReason(string Code, string Message, Guid? ParseRunId, DateTimeOffset At);

/// <summary>Whether a message may be parsed now; shared by the worker, scheduling, and reprocess requests.</summary>
public static class ParseEligibility
{
    public sealed record Hold(string Code, string Message);

    public static async Task<Hold?> HeldAsync(PairrotDbContext db, InboundMessage message, CancellationToken ct)
    {
        if (message.RoutingState != InboundRoutingState.Routed)
        {
            return new Hold("message_not_routed", "The message has no owner.");
        }

        switch (message.ContentState)
        {
            case InboundContentState.Erased:
                return new Hold("content_erased", "The message's content was erased with its tenant.");
            case InboundContentState.Expired:
                return new Hold("content_expired", "The message's content was purged by the payer-data retention limit.");
            case not InboundContentState.Available:
                return new Hold("content_not_available", "The source content has not been retrieved.");
        }

        if (message.HoldReason is { } reason)
        {
            return new Hold("message_held", $"Bank processing of this message is held ({MessageParseJob.Snake(reason)}).");
        }

        if (message.SenderClass is InboundSenderClass.ForwardingConfirmation or InboundSenderClass.Foreign)
        {
            return new Hold("not_a_bank_message", "The message is not from the bank or a registered forwarding mailbox.");
        }

        var processing = await db.InboundAddresses.AsNoTracking()
            .Where(a => a.Id == message.InboundAddressId).Select(a => a.Processing).SingleAsync(ct);
        if (processing == InboundAddressProcessing.PausedForeignMail)
        {
            return new Hold("address_paused", "The address is paused because it received foreign mail; resume it first.");
        }

        var owner = await db.Tenants.AsNoTracking()
            .Where(t => t.Id == message.TenantId)
            .Select(t => new { t.Status, ParentStatus = t.ParentTenant != null ? t.ParentTenant.Status : (TenantStatus?)null })
            .SingleAsync(ct);
        if (owner.Status != TenantStatus.Active || owner.ParentStatus is { } parent && parent != TenantStatus.Active)
        {
            return new Hold("tenant_not_active", "The tenant or its parent is not active; parsing waits for reactivation.");
        }

        var connection = await db.BankConnections.AsNoTracking().Where(b => b.Id == message.BankConnectionId).Select(b => b.Status).SingleAsync(ct);
        return connection == BankConnectionStatus.Archived
            ? new Hold("bank_connection_archived", "The bank connection is archived; its messages are history only.")
            : null;
    }
}
