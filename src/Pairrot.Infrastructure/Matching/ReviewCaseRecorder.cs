using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pairrot.Core.Matching;
using Pairrot.Core.Parsing;
using Pairrot.Core.Transactions;
using Pairrot.Infrastructure.Persistence;

namespace Pairrot.Infrastructure.Matching;

/// <summary>
/// Opens, extends, and reopens review cases for the parser's unresolved results, inside the
/// parse job's transaction under the connection lock. A transaction that enters an open case loses
/// its allocation in the same transaction. Unchanged evidence never recreates a decided case.
/// </summary>
public class ReviewCaseRecorder(PairrotDbContext db, PaymentLedger ledger)
{
    /// <summary>
    /// A recorded transaction is contradicted (changed facts, a rejected source, a conflicting bank
    /// reference). <paramref name="evidence"/> describes the contradiction, e.g. the new facts.
    /// </summary>
    public async Task OnDisputedAsync(
        IncomingTransaction transaction, MessageParseRun run, string code, string message, string evidence, CancellationToken ct)
    {
        var key = Key(code, evidence);
        var open = await OpenCasesWithAsync(transaction.Id, ReviewIssue.Disputed, ct);
        if (open.FirstOrDefault() is { } existing)
        {
            AddMember(existing, null, run.Id);
            if (existing.EvidenceKey != key)
            {
                existing.EvidenceKey = key;
                Touch(existing);
            }

            return;
        }

        var decided = await db.ReviewCases.AnyAsync(c => c.Issue == ReviewIssue.Disputed && c.State == ReviewCaseState.Resolved
            && c.EvidenceKey == key && db.ReviewCaseMembers.Any(m => m.CaseId == c.Id && m.TransactionId == transaction.Id), ct);
        if (decided)
        {
            // The same contradiction was already decided; the decision stands.
            return;
        }

        var reviewCase = NewCase(transaction.TenantId, transaction.BankConnectionId, ReviewIssue.Disputed, null, code, message, key);
        AddMember(reviewCase, transaction.Id, null);
        AddMember(reviewCase, null, run.Id);
        await ledger.LoseEligibilityAsync(transaction, ct);
    }

    /// <summary>
    /// A new transaction resembles others without proof that it is the same movement. All of them
    /// join one open case; earlier records lose any allocation until a person decides.
    /// </summary>
    public async Task OnPossibleDuplicateAsync(
        IncomingTransaction created, IReadOnlyList<IncomingTransaction> similar, MessageParseRun run, CancellationToken ct)
    {
        var related = similar.Where(t => t.Handling is not (TransactionHandling.Rejected or TransactionHandling.Duplicate)).ToList();
        var ids = related.Select(t => t.Id).ToList();
        var existing = await db.ReviewCases
            .Where(c => c.Issue == ReviewIssue.PossibleDuplicate && c.State == ReviewCaseState.Open
                && c.BankConnectionId == created.BankConnectionId
                && db.ReviewCaseMembers.Any(m => m.CaseId == c.Id && m.TransactionId != null && ids.Contains(m.TransactionId.Value)))
            .OrderBy(c => c.Id)
            .FirstOrDefaultAsync(ct);

        var reviewCase = existing ?? NewCase(created.TenantId, created.BankConnectionId, ReviewIssue.PossibleDuplicate, null,
            ParseReasons.PossibleDuplicate,
            "Notifications with the same amount, date, and symbol may describe one payment or several.",
            Key(ParseReasons.PossibleDuplicate, created.ComparisonFingerprint));
        if (existing is not null)
        {
            Touch(existing);
        }

        var members = existing is null
            ? []
            : await db.ReviewCaseMembers.Where(m => m.CaseId == existing.Id && m.TransactionId != null).Select(m => m.TransactionId!.Value).ToListAsync(ct);
        foreach (var transaction in related.Append(created).Where(t => !members.Contains(t.Id)))
        {
            AddMember(reviewCase, transaction.Id, null);
        }

        AddMember(reviewCase, null, run.Id);
        foreach (var transaction in related)
        {
            await ledger.LoseEligibilityAsync(transaction, ct);
        }
    }

    /// <summary>
    /// The parser extracted a payment it could not accept (e.g. an unverified sender). One case per
    /// message: later runs join it; a decided case reopens only when the evidence changes.
    /// </summary>
    public async Task OnUnverifiedCandidateAsync(
        Guid messageId, Guid tenantId, Guid connectionId, MessageParseRun run, TransferCandidate candidate, CancellationToken ct)
    {
        var key = Key(run.ReasonCode, candidate.CriticalFacts);
        var reviewCase = await db.ReviewCases.SingleOrDefaultAsync(c => c.InboundMessageId == messageId, ct);
        if (reviewCase is null)
        {
            reviewCase = NewCase(tenantId, connectionId, ReviewIssue.UnverifiedCandidate, messageId, run.ReasonCode, run.ReasonMessage, key);
            AddMember(reviewCase, null, run.Id);
            return;
        }

        if (reviewCase.EvidenceKey == key)
        {
            if (reviewCase.State == ReviewCaseState.Open)
            {
                AddMember(reviewCase, null, run.Id);
            }

            return;
        }

        AddMember(reviewCase, null, run.Id);
        reviewCase.EvidenceKey = key;
        reviewCase.ReasonCode = run.ReasonCode;
        reviewCase.ReasonMessage = run.ReasonMessage;
        if (reviewCase.State == ReviewCaseState.Resolved)
        {
            reviewCase.State = ReviewCaseState.Open;
            AddDecision(reviewCase, ReviewAction.Reopen, "A new parse run extracted different evidence from the message.");
        }

        Touch(reviewCase);
    }

    /// <summary>The message now yields an accepted transaction itself; its open candidate case is closed by the system.</summary>
    public async Task SupersedeCandidateAsync(Guid messageId, CancellationToken ct)
    {
        var reviewCase = await db.ReviewCases.SingleOrDefaultAsync(c => c.InboundMessageId == messageId && c.State == ReviewCaseState.Open, ct);
        if (reviewCase is null)
        {
            return;
        }

        AddDecision(reviewCase, ReviewAction.Superseded, "A later parse run accepted the message as a transaction.");
        reviewCase.State = ReviewCaseState.Resolved;
        reviewCase.Resolution = ReviewAction.Superseded;
        reviewCase.ResolvedAt = ledger.Now();
        Touch(reviewCase);
    }

    private Task<List<PaymentReviewCase>> OpenCasesWithAsync(Guid transactionId, ReviewIssue issue, CancellationToken ct) =>
        db.ReviewCases
            .Where(c => c.Issue == issue && c.State == ReviewCaseState.Open
                && db.ReviewCaseMembers.Any(m => m.CaseId == c.Id && m.TransactionId == transactionId))
            .OrderBy(c => c.Id)
            .ToListAsync(ct);

    internal PaymentReviewCase NewCase(
        Guid tenantId, Guid connectionId, ReviewIssue issue, Guid? messageId, string code, string message, string key)
    {
        var now = ledger.Now();
        var reviewCase = new PaymentReviewCase
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            BankConnectionId = connectionId,
            Issue = issue,
            InboundMessageId = messageId,
            ReasonCode = code,
            ReasonMessage = message.Length <= 500 ? message : message[..500],
            EvidenceKey = key,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.ReviewCases.Add(reviewCase);
        return reviewCase;
    }

    internal void AddMember(PaymentReviewCase reviewCase, Guid? transactionId, Guid? runId)
    {
        if (db.ReviewCaseMembers.Local.Any(m => m.CaseId == reviewCase.Id && m.TransactionId == transactionId && m.ParseRunId == runId))
        {
            return;
        }

        db.ReviewCaseMembers.Add(new PaymentReviewCaseMember
        {
            Id = Guid.CreateVersion7(),
            CaseId = reviewCase.Id,
            TenantId = reviewCase.TenantId,
            BankConnectionId = reviewCase.BankConnectionId,
            TransactionId = transactionId,
            ParseRunId = runId,
            AddedAt = ledger.Now(),
        });
    }

    private void AddDecision(PaymentReviewCase reviewCase, ReviewAction action, string reason) =>
        db.ReviewDecisions.Add(new PaymentReviewDecision
        {
            Id = Guid.CreateVersion7(),
            CaseId = reviewCase.Id,
            TenantId = reviewCase.TenantId,
            Action = action,
            Reason = reason,
            Actor = ActorLabels.System,
            CaseVersion = reviewCase.Version,
            CreatedAt = ledger.Now(),
        });

    private void Touch(PaymentReviewCase reviewCase)
    {
        reviewCase.Version++;
        reviewCase.UpdatedAt = ledger.Now();
    }

    internal static string Key(string code, string evidence)
    {
        var key = $"{code}|{evidence}";
        return key.Length <= 1000 ? key : key[..1000];
    }

    internal static string Facts(IncomingTransaction t) =>
        TransferCandidate.Facts(t.Amount, t.Currency, t.VariableSymbol, t.BookedOn, t.BankTransactionId);

    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
}
