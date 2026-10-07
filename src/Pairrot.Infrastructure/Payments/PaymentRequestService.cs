using Microsoft.EntityFrameworkCore;
using Pairrot.Core.Banking;
using Pairrot.Core.Payments;
using Pairrot.Core.Tenants;
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
/// <param name="Payable">Fresh payment instructions may be issued: the request is open and its connection active.</param>
public sealed record CreatedPaymentRequest(PaymentRequest Request, bool Replayed, bool Payable);

/// <summary>Changes requested by a PATCH; absent fields stay unchanged.</summary>
public sealed record PaymentRequestChange(Optional<string?> Description = default, Optional<DateTimeOffset?> DueAt = default);

/// <summary>
/// Expected payments of one tenant, scoped like <see cref="Banking.BankConnectionService"/>: the
/// caller's tenant or, for parent management, a direct child. Creating, editing, and cancelling
/// write an audit event in the same transaction; reads and rendering change nothing.
/// </summary>
public class PaymentRequestService(PairrotDbContext db, TenantService tenants)
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
                ? query.Where(p => p.Status == PaymentRequestStatus.Open && p.DueAt < now)
                : query.Where(p => p.Status != PaymentRequestStatus.Open || p.DueAt == null || p.DueAt >= now);
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
    /// decide, and the losers return the winner's record.
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

        if (CheckChangeable(owner.Status) is { } stateError)
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
        if (CheckChangeable(Enum.Parse<TenantStatus>(ownerStatus!, ignoreCase: true)) is { } lockedStateError)
        {
            return lockedStateError;
        }

        var connectionStatus = await LockStatusAsync(db.Database.SqlQuery<string>(
            $"SELECT status AS \"Value\" FROM bank_connections WHERE id = {draft.BankConnectionId} AND tenant_id = {owner.Id} FOR SHARE"), ct);
        if (connectionStatus is null)
        {
            return ServiceError.NotFound("The bank connection was not found.");
        }

        if (connectionStatus != "active")
        {
            return ServiceError.Conflict("bank_connection_archived", "An archived bank connection cannot be used for new payment requests.");
        }

        var connection = await db.BankConnections.AsNoTracking().SingleAsync(b => b.Id == draft.BankConnectionId, ct);
        if (await db.PaymentRequests.AnyAsync(
                p => p.BankConnectionId == connection.Id && p.VariableSymbolKey == draft.VariableSymbolKey, ct))
        {
            return VariableSymbolTaken();
        }

        var payment = draft.Payment with { Iban = connection.Iban };
        var now = Now();
        var request = new PaymentRequest
        {
            Id = Guid.CreateVersion7(),
            TenantId = owner.Id,
            BankConnectionId = connection.Id,
            ExternalId = draft.ExternalId,
            AccountNumber = connection.AccountNumber,
            Iban = connection.Iban,
            Amount = payment.Amount,
            Currency = payment.Currency,
            VariableSymbol = payment.VariableSymbol!,
            VariableSymbolKey = draft.VariableSymbolKey,
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
        tenants.AddAudit(actor, owner.Id, AuditActions.PaymentRequestCreated, new
        {
            PaymentRequestId = request.Id,
            request.ExternalId,
            BankConnectionId = connection.Id,
            request.Amount,
            request.VariableSymbol,
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
            // A concurrent request saved first: the same creation (a replay) or another one with this symbol.
            await transaction.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            return await ReplayAsync(owner.Id, draft, ct)
                ?? (TenantService.IsUniqueViolation(ex, PaymentRequestConfiguration.VariableSymbolUniqueIndex)
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

        if (CheckChangeable(owner!.Status) is { } stateError)
        {
            return stateError;
        }

        if (request.Version != expectedVersion)
        {
            return Stale();
        }

        if (request.Status != PaymentRequestStatus.Open)
        {
            return ServiceError.Conflict("payment_request_cancelled", "A cancelled payment request cannot be changed.");
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

        tenants.AddAudit(actor, owner.Id, AuditActions.PaymentRequestUpdated, new
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

        if (CheckChangeable(owner!.Status) is { } stateError)
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

        var now = Now();
        request.Status = PaymentRequestStatus.Cancelled;
        request.CancellationReason = PaymentRequestRules.NormalizeText(reason);
        request.CancelledAt = now;
        request.UpdatedAt = now;
        request.Version++;
        tenants.AddAudit(actor, owner.Id, AuditActions.PaymentRequestCancelled, new
        {
            PaymentRequestId = request.Id,
            request.ExternalId,
            Reason = request.CancellationReason,
        });

        try
        {
            await db.SaveChangesAsync(ct);
            return request;
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another change won. If it was a cancellation too, this one has nothing left to do.
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
            TenantStatus.Deleted => ServiceError.Conflict("tenant_deleted", "The tenant is deleted and cannot receive payments."),
            TenantStatus.Suspended => ServiceError.Conflict("tenant_not_active", "The tenant is suspended and cannot receive payments."),
            _ when found.Request.Status == PaymentRequestStatus.Cancelled => ServiceError.Conflict(
                "payment_request_cancelled", "The payment request is cancelled; no new payment instructions are issued for it."),
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

    private static async Task<string?> LockStatusAsync(IQueryable<string> query, CancellationToken ct) =>
        (await query.ToListAsync(ct)).SingleOrDefault();

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
    private static ServiceError? CheckChangeable(TenantStatus status) => status switch
    {
        TenantStatus.Deleted => ServiceError.Conflict("tenant_deleted", "The tenant is deleted and cannot be changed."),
        TenantStatus.Suspended => ServiceError.Conflict("tenant_not_active", "The tenant is suspended; reactivate it before changing its payment requests."),
        _ => null,
    };

    private static ServiceError VariableSymbolTaken() => ServiceError.Conflict("variable_symbol_conflict",
        "This variable symbol is already used by another payment request on this bank account (leading zeros are ignored).");

    private static ServiceError Stale() => ServiceError.PreconditionFailed(
        "The payment request has changed since it was read; reload it and try again.");
}
