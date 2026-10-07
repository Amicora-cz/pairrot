using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Pairrot.Core.Banking;
using Pairrot.Core.Inbound;
using Pairrot.Core.Matching;
using Pairrot.Core.Payments;
using Pairrot.Core.Tenants;
using Pairrot.Core.Transactions;
using Pairrot.Infrastructure.Jobs;
using Pairrot.Infrastructure.Persistence;
using Pairrot.Infrastructure.Tenants;

namespace Pairrot.Infrastructure.Matching;

public sealed class MatchingOptions
{
    public const string Section = "Matching";

    /// <summary>How often eligible transactions without a matching job, and unrecorded review cases, are looked for.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Development and Test only: lets test-environment and fixture transactions be matched in an
    /// isolated local database. Allocations keep their test evidence. Refused elsewhere.
    /// </summary>
    public bool AllowTestEvidence { get; set; }
}

/// <summary>Refuses <see cref="MatchingOptions.AllowTestEvidence"/> outside Development and Testing.</summary>
public sealed class MatchingOptionsValidator(IHostEnvironment environment) : IValidateOptions<MatchingOptions>
{
    public ValidateOptionsResult Validate(string? name, MatchingOptions options) =>
        options.AllowTestEvidence && !environment.IsDevelopment() && !environment.IsEnvironment("Testing")
            ? ValidateOptionsResult.Fail("Matching:AllowTestEvidence is only allowed in Development and Testing; test evidence never pays live requests.")
            : ValidateOptionsResult.Success;
}

/// <summary>
/// The one place where allocations change a request's payment result. Every operation works on
/// tracked entities inside the caller's database transaction, which must hold the connection lock
/// (<see cref="LockConnectionAsync"/>): allocation or revocation, transaction handling, the
/// recalculated request, the payment change, and the audit event are saved together or not at all.
/// </summary>
public class PaymentLedger(PairrotDbContext db, BackgroundJobQueue jobs, TenantService tenants, IOptions<MatchingOptions> options)
{
    public static readonly Actor SystemActor = Actor.Administrator(ActorLabels.System);

    public bool AllowTestEvidence => options.Value.AllowTestEvidence;

    public DateTimeOffset Now() => jobs.Now();

    /// <summary>
    /// Serializes parsing, matching, review, and manual allocation of one bank connection for the
    /// rest of the current transaction (the same lock as Feature 007's parse job).
    /// </summary>
    public Task LockConnectionAsync(Guid connectionId, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({connectionId.ToString()}, 7007))", ct);

    public static string Label(Actor actor) => ActorLabels.Of(actor.UserId, actor.ApiKeyId, actor.Description);

    public static string Evidence(IncomingTransaction t) =>
        t.SourceKind == InboundSourceKind.Fixture ? "fixture" : t.Environment == InboundEnvironment.Live ? "live" : "test";

    public Task<bool> InOpenCaseAsync(Guid transactionId, CancellationToken ct) =>
        db.ReviewCaseMembers.AnyAsync(m => m.TransactionId == transactionId
            && db.ReviewCases.Any(c => c.Id == m.CaseId && c.State == ReviewCaseState.Open), ct);

    /// <summary>Why the owner or the account cannot take new allocations, or <c>null</c>.</summary>
    public async Task<string?> LifecycleBlockAsync(Guid tenantId, Guid connectionId, CancellationToken ct)
    {
        var owner = await db.Tenants.AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => new { t.Status, ParentStatus = t.ParentTenant != null ? t.ParentTenant.Status : (TenantStatus?)null })
            .SingleAsync(ct);
        if (owner.Status != TenantStatus.Active || owner.ParentStatus is { } parent && parent != TenantStatus.Active)
        {
            return MatchResults.TenantNotActive;
        }

        var connection = await db.BankConnections.AsNoTracking().Where(b => b.Id == connectionId).Select(b => b.Status).SingleAsync(ct);
        return connection == BankConnectionStatus.Archived ? MatchResults.BankConnectionArchived : null;
    }

    /// <summary>
    /// Allocates the whole transaction to the request. The caller has checked eligibility under the
    /// connection lock; this checks only the arithmetic and records the consequences.
    /// </summary>
    public ServiceResult<PaymentAllocation> Allocate(
        IncomingTransaction transaction, PaymentRequest request, AllocationMethod method, string? reason, Actor actor)
    {
        if (PaymentSummary.Add(request.PaidAmount, transaction.Amount) is not { } paid)
        {
            return ServiceError.Conflict("amount_overflow", "The paid total would exceed the supported range; nothing was saved.");
        }

        var now = Now();
        var before = PaymentSummary.Of(request);
        var allocation = new PaymentAllocation
        {
            Id = Guid.CreateVersion7(),
            TenantId = transaction.TenantId,
            BankConnectionId = transaction.BankConnectionId,
            TransactionId = transaction.Id,
            PaymentRequestId = request.Id,
            Amount = transaction.Amount,
            Currency = transaction.Currency,
            Method = method,
            Reason = reason,
            Evidence = Evidence(transaction),
            CreatedBy = Label(actor),
            CreatedAt = now,
        };
        db.PaymentAllocations.Add(allocation);

        transaction.Handling = TransactionHandling.Allocated;
        transaction.Version++;
        transaction.UpdatedAt = now;

        request.PaidAmount = paid;
        request.Status = PaymentSummary.StatusFor(request.Amount, paid);
        request.Version++;
        request.UpdatedAt = now;
        AddChange(request, before,
            method == AllocationMethod.Automatic ? PaymentChangeCause.AutomaticAllocation : PaymentChangeCause.ManualAllocation,
            allocation.Id, transaction.Id, allocation.Evidence, actor);

        tenants.AddAudit(actor, transaction.TenantId, AuditActions.PaymentAllocated, new
        {
            AllocationId = allocation.Id,
            TransactionId = transaction.Id,
            PaymentRequestId = request.Id,
            request.ExternalId,
            allocation.Amount,
            Method = method.ToString().ToLowerInvariant(),
            Reason = reason,
        });
        return allocation;
    }

    /// <summary>
    /// Revokes an active allocation, keeping it as history, and recalculates the request. The
    /// released transaction is not matched automatically again until someone enables it.
    /// </summary>
    public void Revoke(
        PaymentAllocation allocation, IncomingTransaction transaction, PaymentRequest request,
        string code, string? reason, Actor actor, PaymentChangeCause cause)
    {
        var paid = PaymentSummary.Add(request.PaidAmount, -allocation.Amount)
            ?? throw new InvalidOperationException($"Payment request {request.Id} would have a negative paid amount.");
        var now = Now();
        var before = PaymentSummary.Of(request);

        allocation.State = AllocationState.Revoked;
        allocation.RevokedAt = now;
        allocation.RevokedBy = Label(actor);
        allocation.RevocationCode = code;
        allocation.RevocationReason = reason;
        allocation.Version++;

        transaction.Handling = TransactionHandling.Unallocated;
        transaction.AutoMatchBlocked = true;
        transaction.AutoMatchBlockReason = code;
        transaction.Version++;
        transaction.UpdatedAt = now;

        request.PaidAmount = paid;
        request.Status = PaymentSummary.StatusFor(request.Amount, paid);
        request.Version++;
        request.UpdatedAt = now;
        AddChange(request, before, cause, allocation.Id, transaction.Id, allocation.Evidence, actor);

        tenants.AddAudit(actor, transaction.TenantId, AuditActions.PaymentAllocationRevoked, new
        {
            AllocationId = allocation.Id,
            TransactionId = transaction.Id,
            PaymentRequestId = request.Id,
            request.ExternalId,
            allocation.Amount,
            Code = code,
            Reason = reason,
        });
    }

    /// <summary>
    /// A system correction when a transaction stops being eligible (new review, dispute, or a
    /// reopened case): its active allocation is revoked in the same transaction, even for an
    /// inactive owner, so disputed money is never shown as paid.
    /// </summary>
    public async Task LoseEligibilityAsync(IncomingTransaction transaction, CancellationToken ct)
    {
        transaction.Version++;
        transaction.UpdatedAt = Now();
        if (transaction.Handling != TransactionHandling.Allocated)
        {
            return;
        }

        var allocation = await db.PaymentAllocations
            .SingleAsync(a => a.TransactionId == transaction.Id && a.State == AllocationState.Active, ct);
        var request = await db.PaymentRequests.SingleAsync(r => r.Id == allocation.PaymentRequestId, ct);
        Revoke(allocation, transaction, request, RevocationCodes.TransactionReviewRequired,
            "The transaction needs review; it is not counted until a person allocates it again.",
            SystemActor, PaymentChangeCause.TransactionReviewRequired);
    }

    /// <summary>Appends the next payment change of the request; <paramref name="before"/> is its summary before the change.</summary>
    public void AddChange(
        PaymentRequest request, PaymentSummary before, PaymentChangeCause cause, Guid? allocationId, Guid? transactionId,
        string? evidence, Actor actor)
    {
        request.ChangeSequence++;
        var after = PaymentSummary.Of(request);
        db.PaymentChanges.Add(new PaymentChange
        {
            Id = Guid.CreateVersion7(),
            TenantId = request.TenantId,
            BankConnectionId = request.BankConnectionId,
            PaymentRequestId = request.Id,
            Sequence = request.ChangeSequence,
            ExternalId = request.ExternalId,
            ExpectedAmount = request.Amount,
            Currency = request.Currency,
            StatusBefore = before.Status,
            PaidBefore = before.PaidAmount,
            RemainingBefore = before.RemainingAmount,
            OverpaidBefore = before.OverpaidAmount,
            StatusAfter = after.Status,
            PaidAfter = after.PaidAmount,
            RemainingAfter = after.RemainingAmount,
            OverpaidAfter = after.OverpaidAmount,
            Cause = cause,
            AllocationId = allocationId,
            TransactionId = transactionId,
            Evidence = evidence,
            Actor = Label(actor),
            CreatedAt = Now(),
        });
    }
}
