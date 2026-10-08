using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pairrot.Core.Matching;
using Pairrot.Core.Parsing;
using Pairrot.Core.Tenants;
using Pairrot.Core.Transactions;
using Pairrot.Infrastructure.Persistence;
using Pairrot.Infrastructure.Tenants;
using Pairrot.Infrastructure.Transactions;

namespace Pairrot.Infrastructure.Matching;

/// <summary>A case with its evidence: the transactions and parse runs under review, and every decision so far.</summary>
public sealed record ReviewCaseDetail(
    PaymentReviewCase Case,
    IReadOnlyList<TransactionView> Transactions,
    IReadOnlyList<MessageParseRun> Runs,
    IReadOnlyList<PaymentReviewDecision> Decisions);

/// <param name="TransactionVersions">The version of every transaction in the case, as shown to the caller.</param>
/// <param name="ReasonCode">Operator decisions only: a structured reason, e.g. <c>duplicate_of_foreign_movement</c>.</param>
public sealed record ResolveInput(
    ReviewAction Decision,
    string? Reason,
    int? Version,
    IReadOnlyDictionary<Guid, int>? TransactionVersions,
    Guid? CanonicalTransactionId,
    string? ReasonCode = null);

public sealed record ReopenInput(string? Reason, int? Version, IReadOnlyDictionary<Guid, int>? TransactionVersions);

/// <summary>
/// Review decisions by the payee or its authorized parent. An approval records
/// <c>manual_confirmation</c>: it makes a receipt allocatable by hand, never automatically, and
/// never rewrites authentication evidence, the origin path, sender policy, or forwarding sources.
/// Approval cannot invent payment facts: incomplete, contradictory, or wrong-account evidence stays
/// unresolved until the parser is fixed and the source reprocessed. Feature 013: an
/// <c>account_movement_conflict</c> case is decided by an operator only
/// (<see cref="ResolveAsOperatorAsync"/>), and no tenant decision can approve a transaction that
/// still carries a cross-tenant movement conflict.
/// </summary>
public class ReviewCaseService(
    PairrotDbContext db, TenantService tenants, PaymentLedger ledger, ManualCommands commands, PaymentMatchingService matching,
    ReviewCaseRecorder recorder)
{
    /// <summary>Parser reasons that only say the source was not authenticated; a person may confirm such a payment.</summary>
    private static readonly HashSet<string> ConfirmableReasons =
    [
        ParseReasons.Parsed, ParseReasons.SenderInvalid, ParseReasons.SenderNotAllowed, ParseReasons.NoVerifiedSenders,
        ParseReasons.AuthenticationUnknown, ParseReasons.DmarcNotPass, ParseReasons.ForwarderNotAuthenticated,
        ParseReasons.InvalidVariableSymbol, ParseReasons.OccurredAtInvalid, ParseReasons.OccurredAtAmbiguous,
    ];

    /// <summary>The structured reason an operator gives when a conflicting notification is a copy of another tenant's movement.</summary>
    public const string DuplicateOfForeignMovement = "duplicate_of_foreign_movement";

    public async Task<ServiceResult<Page<PaymentReviewCase>>> ListAsync(
        Guid callerTenantId, Guid? subtenantId, ReviewCaseState? state, int limit, Guid? before, CancellationToken ct = default)
    {
        if (await FindOwnerAsync(callerTenantId, subtenantId, ct) is not { } owner)
        {
            return ServiceError.NotFound();
        }

        var query = db.ReviewCases.AsNoTracking().Where(c => c.TenantId == owner.Id);
        if (state is { } value)
        {
            query = query.Where(c => c.State == value);
        }

        if (before is { } cursor)
        {
            query = query.Where(c => c.Id.CompareTo(cursor) < 0);
        }

        var items = await query.OrderByDescending(c => c.Id).Take(limit + 1).ToListAsync(ct);
        var hasMore = items.Count > limit;
        if (hasMore)
        {
            items.RemoveAt(items.Count - 1);
        }

        return new Page<PaymentReviewCase>(items, hasMore ? items[^1].Id : null);
    }

    public async Task<ServiceResult<ReviewCaseDetail>> GetAsync(Guid callerTenantId, Guid? subtenantId, Guid caseId, CancellationToken ct = default)
    {
        if (await FindCaseAsync(callerTenantId, subtenantId, caseId, ct) is not ({ }, { } reviewCase))
        {
            return ServiceError.NotFound();
        }

        return await DetailAsync(reviewCase, ct);
    }

    public async Task<ServiceResult<CommandOutcome>> ResolveAsync(
        Guid callerTenantId, Guid? subtenantId, Guid caseId, ResolveInput input, CommandRequest command,
        Func<ReviewCaseDetail, object> render, CancellationToken ct = default)
    {
        if (MatchingRules.ValidateReason(input.Reason) is { } reasonError)
        {
            return ServiceError.Validation("invalid_reason", "reason", reasonError);
        }

        if (input.Decision is ReviewAction.Reopen or ReviewAction.Superseded)
        {
            return ServiceError.Validation("invalid_decision", "decision",
                "decision must be confirm_payment, confirm_distinct, mark_duplicate, keep_existing, or reject.");
        }

        if (await FindCaseAsync(callerTenantId, subtenantId, caseId, ct) is not ({ } owner, { } found))
        {
            return ServiceError.NotFound();
        }

        if (found.Issue == ReviewIssue.AccountMovementConflict)
        {
            return ServiceError.Conflict("operator_decision_required",
                "This notification may describe a movement recorded by an earlier registration of the bank account; only an operator can decide it.");
        }

        return await ResolveCoreAsync(owner.Id, found, input with { ReasonCode = null }, command, render, ct);
    }

    /// <summary>
    /// Operator decisions (<c>reviews resolve</c> in the CLI, never over HTTP) on any case, with
    /// the same versions, idempotency, and audit rules as the owner's. On an
    /// <c>account_movement_conflict</c> case only <c>reject</c> with reason code
    /// <c>duplicate_of_foreign_movement</c> (no canonical pointer is saved), or
    /// <c>confirm_distinct</c> of exactly one local lookalike without a proven bank reference
    /// conflict, applies; both recheck the account under its lock.
    /// </summary>
    public async Task<ServiceResult<CommandOutcome>> ResolveAsOperatorAsync(
        Guid caseId, ResolveInput input, CommandRequest command, Func<ReviewCaseDetail, object> render, CancellationToken ct = default)
    {
        if (MatchingRules.ValidateReason(input.Reason) is { } reasonError)
        {
            return ServiceError.Validation("invalid_reason", "reason", reasonError);
        }

        if (input.ReasonCode is { } code && !System.Text.RegularExpressions.Regex.IsMatch(code, "^[a-z][a-z0-9_]{0,49}$"))
        {
            return ServiceError.Validation("invalid_reason_code", "reason_code", "reason_code must be lowercase letters, digits, and underscores.");
        }

        var found = await db.ReviewCases.AsNoTracking().SingleOrDefaultAsync(c => c.Id == caseId, ct);
        return found is null ? ServiceError.NotFound() : await ResolveCoreAsync(found.TenantId, found, input, command, render, ct);
    }

    /// <summary>Operator view of any case with its local evidence, for <c>reviews show</c>.</summary>
    public async Task<ServiceResult<ReviewCaseDetail>> GetAsOperatorAsync(Guid caseId, CancellationToken ct = default)
    {
        var found = await db.ReviewCases.AsNoTracking().SingleOrDefaultAsync(c => c.Id == caseId, ct);
        return found is null ? ServiceError.NotFound() : await DetailAsync(found, ct);
    }

    private async Task<ServiceResult<CommandOutcome>> ResolveCoreAsync(
        Guid ownerId, PaymentReviewCase found, ResolveInput input, CommandRequest command, Func<ReviewCaseDetail, object> render,
        CancellationToken ct)
    {
        if (input.Decision is ReviewAction.Reopen or ReviewAction.Superseded)
        {
            return ServiceError.Validation("invalid_decision", "decision",
                "decision must be confirm_payment, confirm_distinct, mark_duplicate, keep_existing, or reject.");
        }

        if (input.Version is not { } version || input.TransactionVersions is null)
        {
            return ServiceError.PreconditionRequired("Send expected_version and expected_transaction_versions as shown to you.");
        }

        return await commands.RunAsync(ownerId, found.BankAccountId, command, async () =>
        {
            var (reviewCase, transactions, error) = await LoadForDecisionAsync(ownerId, found.Id, version, input.TransactionVersions, ct);
            if (error is not null)
            {
                return error;
            }

            if (reviewCase!.State != ReviewCaseState.Open)
            {
                return ServiceError.Conflict("review_case_resolved", "The review case is already resolved; reopen it to decide again.");
            }

            var reason = input.Reason!.Trim();
            var decision = NewDecision(reviewCase, input.Decision, reason, command.Actor, transactions!);
            decision.ReasonCode = input.ReasonCode;
            var applied = reviewCase.Issue == ReviewIssue.AccountMovementConflict
                ? await ApplyMovementConflictAsync(reviewCase, transactions!, input, decision, reason, command.Actor, ct)
                : await ApplyAsync(reviewCase, transactions!, input, decision, reason, command.Actor, ct);
            if (applied is not null)
            {
                return applied;
            }

            reviewCase.State = ReviewCaseState.Resolved;
            reviewCase.Resolution = input.Decision;
            reviewCase.ResolvedAt = ledger.Now();
            Touch(reviewCase);
            db.ReviewDecisions.Add(decision);
            tenants.AddAudit(command.Actor, ownerId, AuditActions.ReviewCaseResolved, new
            {
                CaseId = reviewCase.Id,
                Decision = MessageParseJob.Snake(input.Decision),
                Reason = reason,
                decision.ReasonCode,
                decision.CanonicalTransactionId,
                decision.CreatedTransactionId,
            });

            await db.SaveChangesAsync(ct);
            return new CommandResult(200, render(await DetailAsync(reviewCase, ct)), new { CaseId = reviewCase.Id, DecisionId = decision.Id });
        }, ct);
    }

    /// <summary>
    /// Reopens a decided case: its transactions lose eligibility again, and any of their active
    /// allocations is revoked in the same transaction. A new resolution keeps all earlier decisions.
    /// </summary>
    public async Task<ServiceResult<CommandOutcome>> ReopenAsync(
        Guid callerTenantId, Guid? subtenantId, Guid caseId, ReopenInput input, CommandRequest command,
        Func<ReviewCaseDetail, object> render, CancellationToken ct = default)
    {
        if (MatchingRules.ValidateReason(input.Reason) is { } reasonError)
        {
            return ServiceError.Validation("invalid_reason", "reason", reasonError);
        }

        if (await FindCaseAsync(callerTenantId, subtenantId, caseId, ct) is not ({ } owner, { } found))
        {
            return ServiceError.NotFound();
        }

        if (input.Version is not { } version || input.TransactionVersions is null)
        {
            return ServiceError.PreconditionRequired("Send expected_version and expected_transaction_versions as shown to you.");
        }

        return await commands.RunAsync(owner.Id, found.BankAccountId, command, async () =>
        {
            var (reviewCase, transactions, error) = await LoadForDecisionAsync(owner.Id, found.Id, version, input.TransactionVersions, ct);
            if (error is not null)
            {
                return error;
            }

            if (reviewCase!.State != ReviewCaseState.Resolved)
            {
                return ServiceError.Conflict("review_case_open", "The review case is already open.");
            }

            var reason = input.Reason!.Trim();
            var decision = NewDecision(reviewCase, ReviewAction.Reopen, reason, command.Actor, transactions!);
            var caseDecisions = await db.ReviewDecisions.Where(d => d.CaseId == reviewCase.Id).Select(d => d.Id).ToListAsync(ct);
            foreach (var transaction in transactions!)
            {
                if (transaction.Handling is TransactionHandling.Rejected or TransactionHandling.Duplicate)
                {
                    SetHandling(transaction, TransactionHandling.Unallocated, null, null);
                }

                if (transaction.ConfirmationDecisionId is { } confirmedBy && caseDecisions.Contains(confirmedBy))
                {
                    transaction.ManuallyConfirmed = false;
                    transaction.ConfirmationDecisionId = null;
                }

                if (reviewCase.Issue == ReviewIssue.AccountMovementConflict)
                {
                    // Reopening keeps the conflict classification; only a new operator decision lifts it.
                    transaction.MovementConflict = true;
                    transaction.AutoMatchBlocked = true;
                    transaction.AutoMatchBlockReason = MatchResults.AccountMovementConflict;
                }

                await ledger.LoseEligibilityAsync(transaction, ct);
            }

            reviewCase.State = ReviewCaseState.Open;
            reviewCase.ResolvedAt = null;
            Touch(reviewCase);
            db.ReviewDecisions.Add(decision);
            tenants.AddAudit(command.Actor, owner.Id, AuditActions.ReviewCaseReopened, new { CaseId = reviewCase.Id, Reason = reason });

            await db.SaveChangesAsync(ct);
            return new CommandResult(200, render(await DetailAsync(reviewCase, ct)), new { CaseId = reviewCase.Id, DecisionId = decision.Id });
        }, ct);
    }

    private async Task<ServiceError?> ApplyAsync(
        PaymentReviewCase reviewCase, List<IncomingTransaction> transactions, ResolveInput input, PaymentReviewDecision decision,
        string reason, Actor actor, CancellationToken ct)
    {
        switch (input.Decision)
        {
            case ReviewAction.ConfirmPayment when reviewCase.Issue == ReviewIssue.UnverifiedCandidate && transactions.Count == 0:
                return await ConfirmCandidateAsync(reviewCase, decision, ct);

            case ReviewAction.ConfirmPayment or ReviewAction.KeepExisting when transactions.Count == 1:
                if (input.Decision == ReviewAction.KeepExisting && reviewCase.Issue != ReviewIssue.Disputed)
                {
                    return NotApplicable("keep_existing resolves a disputed transaction.");
                }

                if (ConflictBlock(transactions) is { } confirmConflict)
                {
                    return confirmConflict;
                }

                Approve(transactions[0], decision);
                return null;

            case ReviewAction.ConfirmDistinct when reviewCase.Issue == ReviewIssue.PossibleDuplicate && transactions.Count >= 2:
                if (ConflictBlock(transactions) is { } distinctConflict)
                {
                    return distinctConflict;
                }

                transactions.ForEach(t => Approve(t, decision));
                return null;

            case ReviewAction.MarkDuplicate when reviewCase.Issue == ReviewIssue.PossibleDuplicate && transactions.Count >= 2:
                if (ConflictBlock(transactions.Where(t => t.Id == input.CanonicalTransactionId)) is { } canonicalConflict)
                {
                    return canonicalConflict;
                }

                return MarkDuplicates(transactions, input.CanonicalTransactionId, decision, reason, actor);

            case ReviewAction.Reject:
                foreach (var transaction in transactions)
                {
                    if (transaction.Handling == TransactionHandling.Allocated)
                    {
                        return ServiceError.Conflict("transaction_already_allocated", "Revoke the allocation before rejecting the transaction.");
                    }

                    SetHandling(transaction, TransactionHandling.Rejected, reason, actor);
                }

                return null;

            default:
                return NotApplicable($"{MessageParseJob.Snake(input.Decision)} does not apply to a {MessageParseJob.Snake(reviewCase.Issue)} case "
                    + $"with {transactions.Count} transaction(s).");
        }
    }

    /// <summary>
    /// The operator branch for <c>account_movement_conflict</c> (Feature 013). Only local evidence
    /// is involved: the foreign record is never read into the decision, linked, or changed.
    /// </summary>
    private async Task<ServiceError?> ApplyMovementConflictAsync(
        PaymentReviewCase reviewCase, List<IncomingTransaction> transactions, ResolveInput input, PaymentReviewDecision decision,
        string reason, Actor actor, CancellationToken ct)
    {
        switch (input.Decision)
        {
            case ReviewAction.Reject:
                if (input.ReasonCode != DuplicateOfForeignMovement)
                {
                    return ServiceError.Validation("invalid_reason_code", "reason_code",
                        $"Rejecting an account movement conflict needs reason_code {DuplicateOfForeignMovement}.");
                }

                foreach (var transaction in transactions)
                {
                    if (transaction.Handling == TransactionHandling.Allocated)
                    {
                        return ServiceError.Conflict("transaction_already_allocated", "Revoke the allocation before rejecting the transaction.");
                    }

                    // A rejected copy, without any pointer to the other tenant's record.
                    SetHandling(transaction, TransactionHandling.Rejected, reason, actor);
                }

                return null;

            case ReviewAction.ConfirmDistinct:
                if (reviewCase.ReasonCode == ParseReasons.BankReferenceConflict || transactions.Count == 0)
                {
                    return NotApplicable("A proven bank reference conflict cannot be approved as a distinct payment; reject it instead.");
                }

                if (transactions.Count != 1)
                {
                    return NotApplicable("confirm_distinct of an account movement conflict applies to exactly one local transaction.");
                }

                var local = transactions[0];
                if (local.BankTransactionId is { } reference && await db.IncomingTransactions.AnyAsync(t => t.Id != local.Id
                        && t.BankAccountId == local.BankAccountId && t.BankProfile == local.BankProfile && t.BankTransactionId == reference, ct))
                {
                    return NotApplicable("The bank reference is recorded by another transaction of this account; it cannot be a distinct payment.");
                }

                local.MovementConflict = false;
                Approve(local, decision);
                return null;

            default:
                return NotApplicable(
                    $"An account movement conflict is decided by reject (reason_code {DuplicateOfForeignMovement}) or by confirm_distinct.");
        }
    }

    /// <summary>A tenant decision may never approve a record that still carries a cross-tenant movement conflict.</summary>
    private static ServiceError? ConflictBlock(IEnumerable<IncomingTransaction> approved) => approved.Any(t => t.MovementConflict)
        ? ServiceError.Conflict(MatchResults.AccountMovementConflict,
            "The transaction may describe a movement recorded by an earlier registration of the bank account; an operator must decide its account movement conflict first.")
        : null;

    /// <summary>
    /// Creates the transaction a person confirmed from an unaccepted candidate. The candidate
    /// must be complete and consistent with the routed account; only authentication may be missing.
    /// Bank references and lookalikes are checked over the whole account (Feature 013): the
    /// tenant's own records must be reviewed there, another tenant's block the confirmation.
    /// </summary>
    private async Task<ServiceError?> ConfirmCandidateAsync(PaymentReviewCase reviewCase, PaymentReviewDecision decision, CancellationToken ct)
    {
        var runIds = await db.ReviewCaseMembers.Where(m => m.CaseId == reviewCase.Id && m.ParseRunId != null)
            .Select(m => m.ParseRunId!.Value).ToListAsync(ct);
        var run = await db.MessageParseRuns.AsNoTracking()
            .Where(r => runIds.Contains(r.Id) && r.Candidate != null)
            .OrderByDescending(r => r.JobGeneration)
            .FirstOrDefaultAsync(ct);
        if (run is null)
        {
            return Incomplete("The case has no extracted payment to confirm.");
        }

        var reasons = JsonSerializer.Deserialize<List<ParseReason>>(run.Reasons, MessageParseJob.Json) ?? [];
        if (reasons.FirstOrDefault(r => !ConfirmableReasons.Contains(r.Code)) is { } blocking)
        {
            return Incomplete($"The extraction has an unresolved problem ({blocking.Code}); fix the parser and reprocess the message instead.");
        }

        var candidate = JsonSerializer.Deserialize<TransferCandidate>(run.Candidate!, MessageParseJob.Json)!;
        var connection = await db.BankConnections.AsNoTracking().SingleAsync(b => b.Id == reviewCase.BankConnectionId, ct);
        if (candidate is not { Amount: > 0, Currency: { } currency, BookedOn: { } bookedOn } || currency != connection.Currency
            || candidate.Destination is not (DestinationEvidence.FullMatch or DestinationEvidence.MaskedSufficient))
        {
            return Incomplete("A confirmed payment needs a positive amount, the account's currency, a booking date, and the destination account.");
        }

        var message = await db.InboundMessages.AsNoTracking().SingleAsync(m => m.Id == reviewCase.InboundMessageId, ct);
        if (await db.TransactionSources.AnyAsync(s => s.InboundMessageId == message.Id, ct))
        {
            return ServiceError.Conflict("message_already_recorded", "The message already supports a transaction; review that transaction instead.");
        }

        if (candidate.BankReference is { } reference
            && await db.IncomingTransactions.AsNoTracking()
                .Where(t => t.BankAccountId == reviewCase.BankAccountId && t.BankProfile == run.BankProfile && t.BankTransactionId == reference)
                .Select(t => (Guid?)t.TenantId).SingleOrDefaultAsync(ct) is { } referenceOwner)
        {
            return referenceOwner == reviewCase.TenantId
                ? ServiceError.Conflict("similar_transaction_exists",
                    "A transaction with this bank reference is already recorded; if this is another copy of it, reject this candidate.")
                : ServiceError.Conflict(MatchResults.AccountMovementConflict, ReviewCaseRecorder.ForeignReferenceMessage);
        }

        var fingerprint = BankMessageEvaluator.ComparisonFingerprint(run.BankProfile!, reviewCase.BankAccountId, candidate);
        var lookalikeOwners = await db.IncomingTransactions.AsNoTracking()
            .Where(t => t.BankAccountId == reviewCase.BankAccountId && t.ComparisonFingerprint == fingerprint
                && t.Handling != TransactionHandling.Rejected && t.Handling != TransactionHandling.Duplicate
                && (t.BankTransactionId == null || candidate.BankReference == null))
            .Select(t => t.TenantId).Distinct().ToListAsync(ct);
        if (lookalikeOwners.Contains(reviewCase.TenantId))
        {
            return ServiceError.Conflict("similar_transaction_exists",
                "A transaction with the same amount, date, and symbol is already recorded; if this is another copy of it, reject this candidate.");
        }

        if (lookalikeOwners.Count > 0)
        {
            return ServiceError.Conflict(MatchResults.AccountMovementConflict, ReviewCaseRecorder.ForeignLookalikeMessage);
        }

        var trust = run.TrustEvidence is null ? null : JsonSerializer.Deserialize<TrustAssessment>(run.TrustEvidence, MessageParseJob.Json);
        var now = ledger.Now();
        var transaction = new IncomingTransaction
        {
            Id = Guid.CreateVersion7(),
            TenantId = reviewCase.TenantId,
            BankConnectionId = reviewCase.BankConnectionId,
            BankAccountId = reviewCase.BankAccountId,
            Amount = candidate.Amount!.Value,
            Currency = currency,
            VariableSymbol = candidate.VariableSymbol,
            VariableSymbolKey = candidate.VariableSymbolKey,
            BankTransactionId = candidate.BankReference,
            SenderAccount = candidate.SenderAccount,
            SenderAccountRaw = BankMessageEvaluator.Limit(candidate.Raw.SenderAccount, 100),
            SenderName = candidate.SenderName,
            Message = candidate.Message,
            BookedOn = bookedOn,
            OccurredAt = candidate.OccurredAt?.ToUniversalTime(),
            ReceivedAt = message.ReceivedAt,
            BankProfile = run.BankProfile!,
            ParserVersion = run.ParserVersion!,
            IngestionStatus = IngestionStatus.ReviewRequired,
            IngestionReasons = JsonSerializer.Serialize(new[]
            {
                new IngestionReason(RevocationCodes.ManualConfirmation,
                    $"Confirmed by a person in review case {reviewCase.Id}: {decision.Reason}", run.Id, now),
            }, MessageParseJob.Json),
            ComparisonFingerprint = fingerprint,
            SourceKind = message.SourceKind,
            Environment = message.Environment,
            OriginPath = trust?.Evidence.OriginPath ?? OriginPath.Direct,
            ManuallyConfirmed = true,
            ConfirmationDecisionId = decision.Id,
            AutoMatchBlocked = true,
            AutoMatchBlockReason = RevocationCodes.ManualConfirmation,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.IncomingTransactions.Add(transaction);
        db.TransactionSources.Add(new TransactionSource
        {
            Id = Guid.CreateVersion7(),
            TransactionId = transaction.Id,
            InboundMessageId = message.Id,
            ParseRunId = run.Id,
            TenantId = transaction.TenantId,
            BankConnectionId = run.BankConnectionId,
            BankAccountId = run.BankAccountId,
            CreatedAt = now,
        });
        recorder.AddMember(reviewCase, transaction.Id, null);
        decision.CreatedTransactionId = transaction.Id;
        return null;
    }

    private ServiceError? MarkDuplicates(
        List<IncomingTransaction> transactions, Guid? canonicalId, PaymentReviewDecision decision, string reason, Actor actor)
    {
        if (transactions.FirstOrDefault(t => t.Id == canonicalId) is not { } canonical)
        {
            return ServiceError.Validation("invalid_canonical_transaction", "canonical_transaction_id",
                "canonical_transaction_id must be one of the case's transactions.");
        }

        if (transactions.Any(t => t.Amount != canonical.Amount || t.Currency != canonical.Currency))
        {
            return ServiceError.Conflict("incompatible_duplicates", "Records with different amounts or currencies cannot be copies of one receipt.");
        }

        if (transactions.Any(t => t.BankTransactionId is { } reference && canonical.BankTransactionId is { } canonicalReference
                && reference != canonicalReference))
        {
            return ServiceError.Conflict("distinct_bank_references", "Records with different proven bank references are different movements.");
        }

        if (transactions.Any(t => t.Handling == TransactionHandling.Allocated))
        {
            return ServiceError.Conflict("transaction_already_allocated", "Revoke the allocation before deciding duplicates.");
        }

        Approve(canonical, decision);
        foreach (var copy in transactions.Where(t => t.Id != canonical.Id))
        {
            SetHandling(copy, TransactionHandling.Duplicate, reason, actor);
            copy.CanonicalTransactionId = canonical.Id;
        }

        decision.CanonicalTransactionId = canonical.Id;
        return null;
    }

    /// <summary>Makes the receipt allocatable by hand (<c>manual_confirmation</c>); automatic matching stays off.</summary>
    private void Approve(IncomingTransaction transaction, PaymentReviewDecision decision)
    {
        if (transaction.Handling is TransactionHandling.Rejected or TransactionHandling.Duplicate)
        {
            SetHandling(transaction, TransactionHandling.Unallocated, null, null);
        }

        transaction.ManuallyConfirmed = true;
        transaction.ConfirmationDecisionId = decision.Id;
        transaction.AutoMatchBlocked = true;
        transaction.AutoMatchBlockReason = RevocationCodes.ManualConfirmation;
        transaction.Version++;
        transaction.UpdatedAt = ledger.Now();
    }

    private void SetHandling(IncomingTransaction transaction, TransactionHandling handling, string? reason, Actor? actor)
    {
        transaction.Handling = handling;
        transaction.HandlingReason = reason;
        transaction.HandledAt = actor is null ? null : ledger.Now();
        transaction.HandledBy = actor is null ? null : PaymentLedger.Label(actor);
        transaction.CanonicalTransactionId = null;
        transaction.AutoMatchBlocked = handling != TransactionHandling.Unallocated || transaction.AutoMatchBlocked;
        transaction.Version++;
        transaction.UpdatedAt = ledger.Now();
    }

    private async Task<(PaymentReviewCase? Case, List<IncomingTransaction>? Transactions, ServiceError? Error)> LoadForDecisionAsync(
        Guid ownerId, Guid caseId, int version, IReadOnlyDictionary<Guid, int> versions, CancellationToken ct)
    {
        var reviewCase = await db.ReviewCases.SingleAsync(c => c.Id == caseId && c.TenantId == ownerId, ct);
        var transactionIds = await db.ReviewCaseMembers.Where(m => m.CaseId == caseId && m.TransactionId != null)
            .Select(m => m.TransactionId!.Value).ToListAsync(ct);
        var transactions = await db.IncomingTransactions.Where(t => transactionIds.Contains(t.Id)).OrderBy(t => t.Id).ToListAsync(ct);

        if (await ledger.LifecycleBlockAsync(ownerId, reviewCase.BankConnectionId, ct) is { } lifecycle)
        {
            return (null, null, PaymentMatchingService.LifecycleError(lifecycle));
        }

        if (transactions.FirstOrDefault(t => !versions.ContainsKey(t.Id)) is { } missing)
        {
            return (null, null, ServiceError.PreconditionRequired($"expected_transaction_versions is missing transaction {missing.Id}."));
        }

        if (reviewCase.Version != version || transactions.Any(t => versions[t.Id] != t.Version))
        {
            return (null, null, PaymentMatchingService.Stale());
        }

        return (reviewCase, transactions, null);
    }

    private PaymentReviewDecision NewDecision(
        PaymentReviewCase reviewCase, ReviewAction action, string reason, Actor actor, List<IncomingTransaction> transactions) => new()
    {
        Id = Guid.CreateVersion7(),
        CaseId = reviewCase.Id,
        TenantId = reviewCase.TenantId,
        Action = action,
        Reason = reason,
        Actor = PaymentLedger.Label(actor),
        CaseVersion = reviewCase.Version,
        Members = JsonSerializer.Serialize(transactions.Select(t => new { TransactionId = t.Id, t.Version }), ManualCommands.Json),
        CreatedAt = ledger.Now(),
    };

    private async Task<ReviewCaseDetail> DetailAsync(PaymentReviewCase reviewCase, CancellationToken ct)
    {
        var members = await db.ReviewCaseMembers.AsNoTracking().Where(m => m.CaseId == reviewCase.Id).ToListAsync(ct);
        var transactionIds = members.Where(m => m.TransactionId != null).Select(m => m.TransactionId!.Value).ToList();
        var runIds = members.Where(m => m.ParseRunId != null).Select(m => m.ParseRunId!.Value).ToList();
        var transactions = await db.IncomingTransactions.AsNoTracking().Where(t => transactionIds.Contains(t.Id)).OrderBy(t => t.Id).ToListAsync(ct);
        var described = await matching.DescribeAsync(transactions, ct);
        var runs = await db.MessageParseRuns.AsNoTracking().Where(r => runIds.Contains(r.Id)).OrderBy(r => r.CompletedAt).ToListAsync(ct);
        var decisions = await db.ReviewDecisions.AsNoTracking().Where(d => d.CaseId == reviewCase.Id).OrderBy(d => d.CreatedAt).ThenBy(d => d.Id).ToListAsync(ct);
        return new ReviewCaseDetail(reviewCase, transactions.Select(t => new TransactionView(t, described[t.Id])).ToList(), runs, decisions);
    }

    private void Touch(PaymentReviewCase reviewCase)
    {
        reviewCase.Version++;
        reviewCase.UpdatedAt = ledger.Now();
    }

    private static ServiceError NotApplicable(string message) => ServiceError.Conflict("decision_not_applicable", message);

    private static ServiceError Incomplete(string message) => ServiceError.Conflict("candidate_not_confirmable", message);

    private Task<Tenant?> FindOwnerAsync(Guid callerTenantId, Guid? subtenantId, CancellationToken ct) =>
        subtenantId is { } childId
            ? tenants.FindSubtenantAsync(callerTenantId, childId, ct)
            : tenants.FindAsync(callerTenantId, ct);

    private async Task<(Tenant? Owner, PaymentReviewCase? Case)> FindCaseAsync(
        Guid callerTenantId, Guid? subtenantId, Guid caseId, CancellationToken ct)
    {
        if (await FindOwnerAsync(callerTenantId, subtenantId, ct) is not { } owner)
        {
            return (null, null);
        }

        var reviewCase = await db.ReviewCases.AsNoTracking().SingleOrDefaultAsync(c => c.Id == caseId && c.TenantId == owner.Id, ct);
        return (owner, reviewCase);
    }
}
