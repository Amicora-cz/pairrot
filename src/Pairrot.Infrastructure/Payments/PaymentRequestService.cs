using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Pairrot.Core.Banking;
using Pairrot.Core.Jobs;
using Pairrot.Core.Matching;
using Pairrot.Core.Payments;
using Pairrot.Core.Tenants;
using Pairrot.Infrastructure.Jobs;
using Pairrot.Infrastructure.Matching;
using Pairrot.Infrastructure.Persistence;
using Pairrot.Infrastructure.Tenants;

namespace Pairrot.Infrastructure.Payments;

/// <param name="VariableSymbol">Matched by its comparison key, so <c>123</c> also finds <c>000123</c>.</param>
public sealed record PaymentRequestFilter(
    string? ExternalId = null,
    PaymentRequestStatus? Status = null,
    Guid? BankConnectionId = null,
    string? VariableSymbol = null,
    bool? Overdue = null);

/// <param name="Replayed">An earlier creation with the same external ID and the same input was returned.</param>
/// <param name="Payable">Fresh payment instructions may be issued: the request is open (nothing paid) and its connection active.</param>
public sealed record CreatedPaymentRequest(PaymentRequest Request, bool Replayed, bool Payable);

/// <summary>Changes requested by a PATCH; absent fields stay unchanged.</summary>
public sealed record PaymentRequestChange(Optional<string?> Description = default, Optional<DateTimeOffset?> DueAt = default);

/// <summary>
/// Expected payments of one tenant, scoped like <see cref="Banking.BankConnectionService"/>: the
/// caller's tenant or, for parent management, a direct child. Creating, editing, and cancelling
/// write an audit event in the same transaction; reads and rendering change nothing. Paid amounts
/// change only through allocations (Feature 009, <see cref="PaymentLedger"/>).
/// </summary>
public class PaymentRequestService(PairrotDbContext db, TenantService tenants, BackgroundJobQueue jobs, PaymentLedger ledger)
{
    public DateTimeOffset Now() => tenants.Now();

    public async Task<ServiceResult<Page<PaymentRequest>>> ListAsync(
        Guid callerTenantId, Guid? subtenantId, PaymentRequestFilter filter, int limit, Guid? before, CancellationToken ct = default)
    {
        if (await FindOwnerAsync(callerTenantId, subtenantId, ct) is not { } owner)
        {
            return ServiceError.NotFound();
        }

        var query = db.PaymentRequests.AsNoTracking().Where(p => p.TenantId == owner.Id);
        if (filter.ExternalId is { } externalId)
        {
            query = query.Where(p => p.ExternalId == externalId);
        }

        if (filter.Status is { } status)
        {
            query = query.Where(p => p.Status == status);
        }

        if (filter.BankConnectionId is { } connectionId)
        {
            query = query.Where(p => p.BankConnectionId == connectionId);
        }

        if (filter.VariableSymbol is { } symbol)
        {
            var key = PaymentRequestRules.VariableSymbolKey(symbol);
            query = query.Where(p => p.VariableSymbolKey == key);
        }

        if (filter.Overdue is { } overdue)
        {
            var now = Now();
            query = overdue
                ? query.Where(p => (p.Status == PaymentRequestStatus.Open || p.Status == PaymentRequestStatus.PartiallyPaid) && p.DueAt < now)
                : query.Where(p => (p.Status != PaymentRequestStatus.Open && p.Status != PaymentRequestStatus.PartiallyPaid)
                    || p.DueAt == null || p.DueAt >= now);
        }

        if (before is { } cursor)
        {
            query = query.Where(p => p.Id.CompareTo(cursor) < 0);
        }

        // Newest first; IDs are UUIDv7, so ordering by ID is ordering by creation time.
        var items = await query.OrderByDescending(p => p.Id).Take(limit + 1).ToListAsync(ct);
        var hasMore = items.Count > limit;
        if (hasMore)
        {
            items.RemoveAt(items.Count - 1);
        }

        return new Page<PaymentRequest>(items, hasMore ? items[^1].Id : null);
    }

    /// <summary>Any request of the owner, also of a suspended or deleted child, for history.</summary>
    public async Task<ServiceResult<PaymentRequest>> GetAsync(
        Guid callerTenantId, Guid? subtenantId, Guid requestId, CancellationToken ct = default)
    {
        if (await FindOwnerAsync(callerTenantId, subtenantId, ct) is not { } owner)
        {
            return ServiceError.NotFound();
        }

        var request = await db.PaymentRequests.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == requestId && p.TenantId == owner.Id, ct);
        return request is null ? ServiceError.NotFound() : request;
    }

    /// <summary>
    /// Creates a request, or returns the one already created with the same external ID and the
    /// same original input. Concurrent identical requests end with one record; the unique indexes
    /// decide, and the losers return the winner's record. Without a supplied symbol, the next free
    /// one of the bank account is assigned (Feature 012); every creation on an account, through any
    /// of its registrations, holds the account's allocator lock until it commits (Feature 013).
    /// </summary>
    public async Task<ServiceResult<CreatedPaymentRequest>> CreateAsync(
        Guid callerTenantId, Guid? subtenantId, PaymentRequestInput input, Actor actor, CancellationToken ct = default)
    {
        var parsed = PaymentRequestRules.Validate(input);
        if (parsed.Error is { } error)
        {
            return ServiceError.Validation(error.Code, error.Field, error.Message);
        }

        var draft = parsed.Draft!;
        if (await FindOwnerAsync(callerTenantId, subtenantId, ct) is not { } owner)
        {
            return ServiceError.NotFound();
        }

        if (CheckChangeable(owner) is { } stateError)
        {
            return stateError;
        }

        if (await ReplayAsync(owner.Id, draft, ct) is { } replay)
        {
            return replay;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // Share locks keep the tenant and the connection from being suspended or archived until
        // this request is saved; one that changed first is read here in its new state.
        var ownerStatus = await LockStatusAsync(
            db.Database.SqlQuery<string>($"SELECT status AS \"Value\" FROM tenants WHERE id = {owner.Id} FOR SHARE"), ct);
        if (CheckChangeable(owner, Enum.Parse<TenantStatus>(ownerStatus!, ignoreCase: true)) is { } lockedStateError)
        {
            return lockedStateError;
        }

        var locked = (await db.Database.SqlQuery<ConnectionLock>(
                $"SELECT status, bank_account_id FROM bank_connections WHERE id = {draft.BankConnectionId} AND tenant_id = {owner.Id} FOR SHARE")
            .ToListAsync(ct)).SingleOrDefault();
        if (locked is null)
        {
            return ServiceError.NotFound("The bank connection was not found.");
        }

        if (locked.Status != "active")
        {
            return ServiceError.Conflict("bank_connection_archived", "An archived bank connection cannot be used for new payment requests.");
        }

        var accountId = locked.BankAccountId;
        var cursor = await LockAllocatorAsync(accountId, ct);

        // A creation that committed while this one waited for the lock is replayed, not duplicated.
        if (await ReplayAsync(owner.Id, draft, ct) is { } lockedReplay)
        {
            return lockedReplay;
        }

        var now = Now();
        string variableSymbol;
        string variableSymbolKey;
        if (draft.VariableSymbolKey is { } suppliedKey)
        {
            // A supplied symbol is reserved as it is and never moves the cursor. Any registration of
            // the account counts, in any tenant: the old instructions may still be paid.
            if (await db.PaymentRequests.AnyAsync(p => p.BankAccountId == accountId && p.VariableSymbolKey == suppliedKey, ct))
            {
                return VariableSymbolTaken();
            }

            variableSymbol = draft.Payment.VariableSymbol!;
            variableSymbolKey = suppliedKey;
        }
        else
        {
            var series = VariableSymbolSeries.At(now);
            if (await FindFreeVariableSymbolAsync(accountId, series.ResumeAt(cursor), series.Last, ct) is not { } assigned)
            {
                return ServiceError.Conflict("variable_symbol_space_exhausted",
                    "No automatic variable symbol is left for this year on this bank account; supply an unused variable_symbol instead.");
            }

            await db.Database.ExecuteSqlAsync(
                $"UPDATE variable_symbol_allocators SET next_candidate = {assigned + 1} WHERE bank_account_id = {accountId}",
                ct);
            variableSymbol = assigned.ToString(CultureInfo.InvariantCulture);
            variableSymbolKey = variableSymbol;
        }

        var connection = await db.BankConnections.AsNoTracking().SingleAsync(b => b.Id == draft.BankConnectionId, ct);
        var payment = draft.Payment with { Iban = connection.Iban, VariableSymbol = variableSymbol };
        var request = new PaymentRequest
        {
            Id = Guid.CreateVersion7(),
            TenantId = owner.Id,
            BankConnectionId = connection.Id,
            BankAccountId = connection.BankAccountId,
            ExternalId = draft.ExternalId,
            AccountNumber = connection.AccountNumber,
            Iban = connection.Iban,
            Amount = payment.Amount,
            Currency = payment.Currency,
            VariableSymbol = variableSymbol,
            VariableSymbolKey = variableSymbolKey,
            VariableSymbolSource = draft.VariableSymbolSource,
            Message = payment.Message,
            Description = draft.Description,
            DueAt = draft.DueAt,
            CreationFingerprint = draft.Fingerprint,
            QrPayload = payment.Payload,
            QrFormat = SpaydPayment.FormatName,
            QrGeneratorVersion = PaymentRequestRules.QrGeneratorVersion,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.PaymentRequests.Add(request);

        // A receipt may have arrived before the request; matching looks for it once the request is saved.
        jobs.Add(BackgroundJobTypes.MatchPaymentRequest, request.Id, 1, BackgroundJobQueue.SystemRequester);
        tenants.AddAudit(actor, owner.Id, AuditActions.PaymentRequestCreated, new
        {
            PaymentRequestId = request.Id,
            request.ExternalId,
            BankConnectionId = connection.Id,
            connection.BankAccountId,
            request.Amount,
            request.VariableSymbol,
            VariableSymbolSource = SnakeCaseEnum<VariableSymbolSource>.ToName(request.VariableSymbolSource),
        });

        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return new CreatedPaymentRequest(request, Replayed: false, Payable: true);
        }
        catch (DbUpdateException ex) when (
            TenantService.IsUniqueViolation(ex, PaymentRequestConfiguration.ExternalIdUniqueIndex)
            || TenantService.IsUniqueViolation(ex, PaymentRequestConfiguration.VariableSymbolUniqueIndex))
        {
            // A concurrent request saved first: the same creation (a replay), or one with this
            // external ID on another connection, or one with this symbol. Nothing of this one is kept.
            await transaction.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            return await ReplayAsync(owner.Id, draft, ct)
                ?? (draft.VariableSymbolSource == VariableSymbolSource.Supplied
                    && TenantService.IsUniqueViolation(ex, PaymentRequestConfiguration.VariableSymbolUniqueIndex)
                    ? VariableSymbolTaken()
                    : ServiceError.Conflict("concurrent_modification", "The payment request was created concurrently; retry the request."));
        }
    }

    /// <summary>Changes the description or due time of an open request whose version is <paramref name="expectedVersion"/>.</summary>
    public async Task<ServiceResult<PaymentRequest>> UpdateAsync(
        Guid callerTenantId, Guid? subtenantId, Guid requestId, int expectedVersion, PaymentRequestChange change, Actor actor,
        CancellationToken ct = default)
    {
        if (change.Description.HasValue && PaymentRequestRules.ValidateDescription(change.Description.Value) is { } descriptionError)
        {
            return ServiceError.Validation(PaymentRequestErrors.InvalidDescription, "description", descriptionError);
        }

        var (owner, request) = await FindForChangeAsync(callerTenantId, subtenantId, requestId, ct);
        if (request is null)
        {
            return ServiceError.NotFound();
        }

        if (CheckChangeable(owner!) is { } stateError)
        {
            return stateError;
        }

        if (request.Version != expectedVersion)
        {
            return Stale();
        }

        if (request.Status == PaymentRequestStatus.Cancelled)
        {
            return ServiceError.Conflict("payment_request_cancelled", "A cancelled payment request cannot be changed.");
        }

        if (!PaymentRequestRules.IsEditable(request.Status))
        {
            return ServiceError.Conflict("payment_request_settled",
                "A paid or overpaid payment request is read-only; only its allocations can be corrected.");
        }

        var changes = new Dictionary<string, object?>();
        if (change.Description.HasValue && PaymentRequestRules.NormalizeText(change.Description.Value) is var description
            && description != request.Description)
        {
            changes["description"] = new { From = request.Description, To = description };
            request.Description = description;
        }

        if (change.DueAt.HasValue && PaymentRequestRules.NormalizeDueAt(change.DueAt.Value) is var dueAt && dueAt != request.DueAt)
        {
            changes["due_at"] = new { From = request.DueAt, To = dueAt };
            request.DueAt = dueAt;
        }

        if (changes.Count == 0)
        {
            return request;
        }

        tenants.AddAudit(actor, owner!.Id, AuditActions.PaymentRequestUpdated, new
        {
            PaymentRequestId = request.Id,
            request.ExternalId,
            Changes = changes,
        });
        request.Version++;
        request.UpdatedAt = Now();

        try
        {
            await db.SaveChangesAsync(ct);
            return request;
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return Stale();
        }
    }

    /// <summary>
    /// Cancels an open request, keeping the record and its symbol. Cancelling a cancelled request
    /// has no further effect and needs no version.
    /// </summary>
    public async Task<ServiceResult<PaymentRequest>> CancelAsync(
        Guid callerTenantId, Guid? subtenantId, Guid requestId, int? expectedVersion, string? reason, Actor actor,
        CancellationToken ct = default)
    {
        if (PaymentRequestRules.ValidateCancellationReason(reason) is { } reasonError)
        {
            return ServiceError.Validation(PaymentRequestErrors.InvalidReason, "reason", reasonError);
        }

        var (owner, request) = await FindForChangeAsync(callerTenantId, subtenantId, requestId, ct);
        if (request is null)
        {
            return ServiceError.NotFound();
        }

        if (CheckChangeable(owner!) is { } stateError)
        {
            return stateError;
        }

        if (request.Status == PaymentRequestStatus.Cancelled)
        {
            return request;
        }

        if (expectedVersion is not { } version)
        {
            return ServiceError.PreconditionRequired("Send the request's current version in If-Match (the ETag from GET).");
        }

        if (request.Version != version)
        {
            return Stale();
        }

        // Allocations hold received money; they must be revoked first. A concurrent allocation
        // changes the version, so the save below fails instead of cancelling a paid request.
        if (request.PaidAmount > 0 || await db.PaymentAllocations.AnyAsync(
                a => a.PaymentRequestId == request.Id && a.State == AllocationState.Active, ct))
        {
            return ServiceError.Conflict("payment_request_has_allocations",
                "The payment request has received payments; revoke their allocations before cancelling it.");
        }

        // The payment change and its webhook deliveries commit with the cancellation.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var now = Now();
        var before = PaymentSummary.Of(request);
        request.Status = PaymentRequestStatus.Cancelled;
        request.CancellationReason = PaymentRequestRules.NormalizeText(reason);
        request.CancelledAt = now;
        request.UpdatedAt = now;
        request.Version++;
        await ledger.AddChangeAsync(request, before, PaymentChangeCause.Cancelled, null, null, null, actor, ct);
        tenants.AddAudit(actor, owner!.Id, AuditActions.PaymentRequestCancelled, new
        {
            PaymentRequestId = request.Id,
            request.ExternalId,
            Reason = request.CancellationReason,
        });

        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return request;
        }
        catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException
            || TenantService.IsUniqueViolation(ex, PaymentChangeConfiguration.SequenceUniqueIndex))
        {
            // Another change won (its payment change took the same sequence number). If it was a
            // cancellation too, this one has nothing left to do.
            await transaction.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            var current = await db.PaymentRequests.AsNoTracking().SingleAsync(p => p.Id == requestId, ct);
            return current.Status == PaymentRequestStatus.Cancelled ? current : Stale();
        }
    }

    /// <summary>
    /// A request whose saved instructions may be handed out: the owner is active, the request is
    /// open, and its bank connection is active. Archiving a connection does not cancel its
    /// requests, but blocks fresh instructions until an operator resolves them.
    /// </summary>
    public async Task<ServiceResult<PaymentRequest>> GetPayableAsync(
        Guid callerTenantId, Guid? subtenantId, Guid requestId, CancellationToken ct = default)
    {
        if (await FindOwnerAsync(callerTenantId, subtenantId, ct) is not { } owner)
        {
            return ServiceError.NotFound();
        }

        var found = await (
            from p in db.PaymentRequests.AsNoTracking()
            join b in db.BankConnections on p.BankConnectionId equals b.Id
            where p.Id == requestId && p.TenantId == owner.Id
            select new { Request = p, ConnectionStatus = b.Status }).SingleOrDefaultAsync(ct);
        if (found is null)
        {
            return ServiceError.NotFound();
        }

        return owner.Status switch
        {
            TenantStatus.Deleted => TenantService.TenantDeleted(owner, "The tenant is deleted and cannot receive payments."),
            TenantStatus.Suspended => ServiceError.Conflict("tenant_not_active", "The tenant is suspended and cannot receive payments."),
            _ when found.Request.Status == PaymentRequestStatus.Cancelled => ServiceError.Conflict(
                "payment_request_cancelled", "The payment request is cancelled; no new payment instructions are issued for it."),
            _ when found.Request.Status != PaymentRequestStatus.Open => ServiceError.Conflict(
                "payment_instructions_not_available",
                "The payment request has already received a payment; its original full-amount instructions are not issued again."),
            _ when found.ConnectionStatus == BankConnectionStatus.Archived => ServiceError.Conflict(
                "bank_connection_archived", "The payment request's bank connection is archived; no new payment instructions are issued for it."),
            _ => found.Request,
        };
    }

    /// <summary>
    /// The existing request for this external ID, if any: the current record when the original
    /// input matches, otherwise a conflict. Later edits and cancellation are kept, never undone.
    /// </summary>
    private async Task<ServiceResult<CreatedPaymentRequest>?> ReplayAsync(Guid ownerId, PaymentRequestDraft draft, CancellationToken ct)
    {
        var existing = await (
            from p in db.PaymentRequests.AsNoTracking()
            join b in db.BankConnections on p.BankConnectionId equals b.Id
            where p.TenantId == ownerId && p.ExternalId == draft.ExternalId
            select new { Request = p, ConnectionStatus = b.Status }).SingleOrDefaultAsync(ct);

        if (existing is null)
        {
            return null;
        }

        if (existing.Request.CreationFingerprint != draft.Fingerprint)
        {
            return ServiceError.Conflict("external_id_conflict",
                "A payment request with this external_id already exists with different details.");
        }

        var payable = existing.Request.Status == PaymentRequestStatus.Open && existing.ConnectionStatus == BankConnectionStatus.Active;
        return new CreatedPaymentRequest(existing.Request, Replayed: true, payable);
    }

    /// <summary>
    /// Locks the account's allocator until the transaction ends, creating it with the internal
    /// initial cursor 1 on first use. Automatic assignment maps it to the current year's series.
    /// Concurrent first uses insert one row; the others wait for it.
    /// </summary>
    private async Task<long> LockAllocatorAsync(Guid accountId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO variable_symbol_allocators (bank_account_id, next_candidate) VALUES ({accountId}, 1) ON CONFLICT (bank_account_id) DO NOTHING",
            ct);
        return await db.Database.SqlQuery<long>(
                $"SELECT next_candidate AS \"Value\" FROM variable_symbol_allocators WHERE bank_account_id = {accountId} FOR UPDATE")
            .SingleAsync(ct);
    }

    /// <summary>
    /// The lowest symbol at or above <paramref name="cursor"/> that no request on the account holds
    /// (leading zeros ignored, any registration, any tenant), or <c>null</c> when none is left up to
    /// this year's maximum. Only the cursor and the successors of symbols taken at or above it can be the
    /// first gap, so both lookups use the (account, number) index.
    /// </summary>
    private async Task<long?> FindFreeVariableSymbolAsync(Guid accountId, long cursor, long maximum, CancellationToken ct) =>
        await db.Database.SqlQuery<long?>($"""
            SELECT min(c.candidate) AS "Value"
            FROM (
                SELECT {cursor}::bigint AS candidate
                UNION ALL
                SELECT p.variable_symbol_number + 1
                FROM payment_requests p
                WHERE p.bank_account_id = {accountId}
                  AND p.variable_symbol_number >= {cursor} AND p.variable_symbol_number < {maximum}
            ) c
            WHERE c.candidate <= {maximum}
              AND NOT EXISTS (
                SELECT 1 FROM payment_requests q
                WHERE q.bank_account_id = {accountId} AND q.variable_symbol_number = c.candidate)
            """).SingleAsync(ct);

    private static async Task<string?> LockStatusAsync(IQueryable<string> query, CancellationToken ct) =>
        (await query.ToListAsync(ct)).SingleOrDefault();

    /// <summary>A bank connection's state read under <c>FOR SHARE</c>.</summary>
    private sealed class ConnectionLock
    {
        public string Status { get; set; } = "";
        public Guid BankAccountId { get; set; }
    }

    private Task<Tenant?> FindOwnerAsync(Guid callerTenantId, Guid? subtenantId, CancellationToken ct) =>
        subtenantId is { } childId
            ? tenants.FindSubtenantAsync(callerTenantId, childId, ct)
            : tenants.FindAsync(callerTenantId, ct);

    private async Task<(Tenant? Owner, PaymentRequest? Request)> FindForChangeAsync(
        Guid callerTenantId, Guid? subtenantId, Guid requestId, CancellationToken ct)
    {
        if (await FindOwnerAsync(callerTenantId, subtenantId, ct) is not { } owner)
        {
            return (null, null);
        }

        var request = await db.PaymentRequests.SingleOrDefaultAsync(p => p.Id == requestId && p.TenantId == owner.Id, ct);
        return (owner, request);
    }

    /// <summary>A deleted tenant's requests are history; a suspended child's can be inspected by its parent but not changed.</summary>
    private static ServiceError? CheckChangeable(Tenant owner, TenantStatus? lockedStatus = null) => (lockedStatus ?? owner.Status) switch
    {
        TenantStatus.Deleted => TenantService.TenantDeleted(owner),
        TenantStatus.Suspended => ServiceError.Conflict("tenant_not_active", "The tenant is suspended; reactivate it before changing its payment requests."),
        _ => null,
    };

    private static ServiceError VariableSymbolTaken() => ServiceError.Conflict("variable_symbol_conflict",
        "This variable symbol is already used by another payment request on this bank account (leading zeros are ignored).");

    private static ServiceError Stale() => ServiceError.PreconditionFailed(
        "The payment request has changed since it was read; reload it and try again.");
}
