using Microsoft.EntityFrameworkCore;
using Pairrot.Core.Banking;
using Pairrot.Core.Inbound;
using Pairrot.Core.Jobs;
using Pairrot.Core.Tenants;
using Pairrot.Infrastructure.Inbound;
using Pairrot.Infrastructure.Jobs;
using Pairrot.Infrastructure.Persistence;
using Pairrot.Infrastructure.Tenants;

namespace Pairrot.Infrastructure.Banking;

public sealed record BankConnectionInput(string? AccountNumber, string? Iban, string? Currency, string? Label);

/// <summary>
/// Separate facts about reception rather than one "connected" flag. A received message proves
/// delivery to the address, not that a bank sent it or that a payment arrived.
/// </summary>
public sealed record ReceptionFacts(
    DateTimeOffset? LastMessageReceivedAt,
    DateTimeOffset? LastContentRetrievedAt,
    string? LastRetrievalErrorCode,
    string? LastRetrievalErrorMessage,
    DateTimeOffset? LastRetrievalErrorAt)
{
    public static readonly ReceptionFacts None = new(null, null, null, null, null);
}

/// <param name="Address">The connection's current (active) notification address, if any.</param>
/// <param name="ReplacedStub">Address assignment only: <c>true</c> when a stub address was retired for a live one.</param>
public sealed record BankConnectionView(
    BankConnection Connection, InboundAddress? Address, ReceptionFacts Reception, bool? ReplacedStub = null);

/// <summary>Operator view of one registration of an account (<c>accounts show</c>), across tenants.</summary>
public sealed record AccountRegistration(BankConnection Connection, Tenant Owner, string? ParentExternalId);

/// <summary>Operator view of a physical account and every registration of it, oldest first.</summary>
public sealed record AccountRegistrations(BankAccount Account, IReadOnlyList<AccountRegistration> Registrations);

/// <summary>
/// Bank connections of one tenant. Every method takes the caller's tenant and, for parent
/// management, the ID of a direct child; the owner is resolved here and checked again together
/// with the connection on every operation. Callers are authorized by the API layer first.
/// </summary>
public class BankConnectionService(
    PairrotDbContext db,
    TenantService tenants,
    InboundProviderRegistry providers,
    IEnumerable<IInboundAddressProvisioner> provisioners,
    InboundRouter router,
    BackgroundJobQueue jobs)
{
    private const int AddressAttempts = 3;

    /// <summary>The adapter kind of the default instance, e.g. <c>stub</c> or <c>resend</c>.</summary>
    public string ProviderName => providers.Default.Kind;

    public async Task<ServiceResult<IReadOnlyList<BankConnectionView>>> ListAsync(
        Guid callerTenantId, Guid? subtenantId, bool includeArchived, CancellationToken ct = default)
    {
        if (await FindOwnerAsync(callerTenantId, subtenantId, ct) is not { } owner)
        {
            return ServiceError.NotFound();
        }

        var query = db.BankConnections.AsNoTracking().Where(b => b.TenantId == owner.Id);
        if (!includeArchived)
        {
            query = query.Where(b => b.Status == BankConnectionStatus.Active);
        }

        // IDs are UUIDv7, so ordering by ID is ordering by creation time.
        return await ToViewsAsync(await query.OrderBy(b => b.Id).ToListAsync(ct), ct);
    }

    public async Task<ServiceResult<BankConnectionView>> GetAsync(
        Guid callerTenantId, Guid? subtenantId, Guid connectionId, CancellationToken ct = default)
    {
        if (await FindOwnerAsync(callerTenantId, subtenantId, ct) is not { } owner)
        {
            return ServiceError.NotFound();
        }

        var connection = await db.BankConnections.AsNoTracking()
            .SingleOrDefaultAsync(b => b.Id == connectionId && b.TenantId == owner.Id, ct);
        return connection is null ? ServiceError.NotFound() : await ToViewAsync(connection, ct);
    }

    public async Task<ServiceResult<BankConnectionView>> CreateAsync(
        Guid callerTenantId, Guid? subtenantId, BankConnectionInput input, Actor actor, CancellationToken ct = default)
    {
        if (TenantRules.ValidateLabel(input.Label) is { } labelError)
        {
            return ServiceError.Validation("invalid_request", "label", labelError);
        }

        if (input.Currency is { } currency && currency.Trim().ToUpperInvariant() != CzechBankAccount.Currency)
        {
            return ServiceError.Validation(
                BankAccountErrors.UnsupportedCurrency, "currency", $"Only {CzechBankAccount.Currency} is supported.");
        }

        var parsed = CzechBankAccount.Parse(input.AccountNumber, input.Iban);
        if (parsed.Error is { } accountError)
        {
            return ServiceError.Validation(accountError.Code, accountError.Field, accountError.Message);
        }

        if (await FindOwnerAsync(callerTenantId, subtenantId, ct) is not { } owner)
        {
            return ServiceError.NotFound();
        }

        if (CheckChangeable(owner) is { } stateError)
        {
            return stateError;
        }

        var account = parsed.Account!;
        if (await AccountTakenAsync(account.Iban, owner.Id, ct) is { } taken)
        {
            return taken;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var now = tenants.Now();
        var accountId = await EnsureAccountAsync(account, now, ct);
        var connection = new BankConnection
        {
            Id = Guid.CreateVersion7(),
            TenantId = owner.Id,
            BankAccountId = accountId,
            Label = string.IsNullOrWhiteSpace(input.Label) ? null : input.Label.Trim(),
            AccountNumber = account.AccountNumber,
            Iban = account.Iban,
            Currency = CzechBankAccount.Currency,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.BankConnections.Add(connection);
        tenants.AddAudit(actor, owner.Id, AuditActions.BankConnectionCreated, new
        {
            ConnectionId = connection.Id,
            BankAccountId = accountId,
            connection.AccountNumber,
            connection.Label,
        });

        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return new BankConnectionView(connection, null, ReceptionFacts.None);
        }
        catch (DbUpdateException ex) when (TenantService.IsUniqueViolation(ex, BankConnectionConfiguration.OneActivePerAccountIndex))
        {
            // A concurrent creation or restore made another registration of the account active first.
            await transaction.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            return await AccountTakenAsync(account.Iban, owner.Id, ct) ?? AccountUnavailable();
        }
    }

    /// <summary>
    /// Returns an archived connection of an active tenant to <c>active</c>, unless another
    /// registration of its account is active anywhere. Its current address stays; a retired address
    /// stays retired. Messages held only because the connection was archived are re-evaluated in the
    /// same transaction and queued for parsing when nothing else holds them; nothing already parsed
    /// is parsed again. Restoring an active connection has no further effect.
    /// </summary>
    public async Task<ServiceResult<BankConnectionView>> RestoreAsync(
        Guid callerTenantId, Guid? subtenantId, Guid connectionId, Actor actor, CancellationToken ct = default)
    {
        var (owner, connection) = await FindForChangeAsync(callerTenantId, subtenantId, connectionId, ct);
        if (connection is null)
        {
            return ServiceError.NotFound();
        }

        if (connection.Status == BankConnectionStatus.Active)
        {
            return await ToViewAsync(connection, ct);
        }

        if (connection.ErasedAt is not null || owner!.ErasureRequestedAt is not null)
        {
            return ServiceError.Conflict("bank_connection_erased", "The bank connection was erased with its tenant and cannot be restored.");
        }

        if (owner.Status != TenantStatus.Active)
        {
            return ServiceError.Conflict("tenant_not_active", "The tenant is suspended or deleted; its bank connections cannot be restored.");
        }

        if (await db.BankConnections.AnyAsync(b => b.BankAccountId == connection.BankAccountId && b.Status == BankConnectionStatus.Active, ct))
        {
            return AccountUnavailable();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var now = tenants.Now();
        var previousReason = connection.ArchivedReason;
        connection.Status = BankConnectionStatus.Active;
        connection.ArchivedAt = null;
        connection.ArchivedReason = null;
        connection.UpdatedAt = now;
        try
        {
            // The connection must be active before held messages are evaluated against it.
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A concurrent request changed the connection first, e.g. the same restore by the parent.
            await transaction.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            var current = await db.BankConnections.AsNoTracking().SingleAsync(b => b.Id == connectionId, ct);
            return current.Status == BankConnectionStatus.Active
                ? await ToViewAsync(current, ct)
                : ServiceError.Conflict("concurrent_modification", "The bank connection was changed concurrently; retry the request.");
        }
        catch (DbUpdateException ex) when (TenantService.IsUniqueViolation(ex, BankConnectionConfiguration.OneActivePerAccountIndex))
        {
            await transaction.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            return AccountUnavailable();
        }

        var (released, queued) = await ReleaseArchivedHoldsAsync(connection.Id, InboundFetchQueue.RequesterOf(actor), ct);
        tenants.AddAudit(actor, owner.Id, AuditActions.BankConnectionRestored, new
        {
            ConnectionId = connection.Id,
            connection.BankAccountId,
            ArchivedReason = previousReason is { } reason ? SnakeCaseEnum<BankConnectionArchivedReason>.ToName(reason) : null,
            ReleasedMessageCount = released,
            QueuedParseCount = queued,
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return await ToViewAsync(connection, ct);
    }

    /// <summary>
    /// Operator view of an account given as an IBAN or a domestic number: every registration in
    /// every tenant, with status and dates. Never exposed over HTTP.
    /// </summary>
    public async Task<ServiceResult<AccountRegistrations>> FindRegistrationsAsync(string accountText, CancellationToken ct = default)
    {
        var parsed = accountText.Contains('/') ? CzechBankAccount.ParseDomestic(accountText) : CzechBankAccount.ParseIban(accountText);
        if (parsed.Error is { } error)
        {
            return ServiceError.Validation(error.Code, error.Field, error.Message);
        }

        var account = await db.BankAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.Iban == parsed.Account!.Iban, ct);
        if (account is null)
        {
            return ServiceError.NotFound("No registration of this account was ever made.");
        }

        var registrations = await (
            from b in db.BankConnections.AsNoTracking()
            join t in db.Tenants.AsNoTracking() on b.TenantId equals t.Id
            where b.BankAccountId == account.Id
            orderby b.Id
            select new { Connection = b, Owner = t, ParentExternalId = t.ParentTenant != null ? t.ParentTenant.ExternalId : null })
            .ToListAsync(ct);
        return new AccountRegistrations(account,
            registrations.Select(r => new AccountRegistration(r.Connection, r.Owner, r.ParentExternalId)).ToList());
    }

    /// <summary>
    /// Operator release (<c>accounts release</c>): archives a registration with reason
    /// <c>operator</c>, e.g. when the wrong tenant squats an account. Audited on the owner.
    /// Releasing an archived connection has no further effect.
    /// </summary>
    public async Task<ServiceResult<BankConnection>> ReleaseAsOperatorAsync(
        Guid connectionId, string? reason, Actor actor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return ServiceError.Validation("invalid_reason", "reason", "--reason is required.");
        }

        var connection = await db.BankConnections.SingleOrDefaultAsync(b => b.Id == connectionId, ct);
        if (connection is null)
        {
            return ServiceError.NotFound();
        }

        if (connection.Status == BankConnectionStatus.Archived)
        {
            return connection;
        }

        var now = tenants.Now();
        connection.Status = BankConnectionStatus.Archived;
        connection.ArchivedAt = now;
        connection.ArchivedReason = BankConnectionArchivedReason.Operator;
        connection.UpdatedAt = now;
        tenants.AddAudit(actor, connection.TenantId, AuditActions.BankConnectionArchived, new
        {
            ConnectionId = connection.Id,
            Reason = "operator",
            Note = reason.Trim(),
        });
        return await SaveAsync(connection, ct);
    }

    /// <summary>
    /// Re-evaluates the messages of a just-restored connection that are held only because it was
    /// archived. A message that nothing else holds is queued for parsing if it was never parsed.
    /// </summary>
    private async Task<(int Released, int Queued)> ReleaseArchivedHoldsAsync(Guid connectionId, string requestedBy, CancellationToken ct)
    {
        var held = await db.InboundMessages
            .Where(m => m.BankConnectionId == connectionId && m.HoldReason == InboundHoldReason.BankConnectionArchived)
            .ToListAsync(ct);
        var addresses = await db.InboundAddresses.AsNoTracking()
            .Where(a => a.BankConnectionId == connectionId)
            .ToDictionaryAsync(a => a.Id, ct);
        var (released, queued) = (0, 0);
        foreach (var message in held)
        {
            var hold = message.InboundAddressId is { } addressId && addresses.TryGetValue(addressId, out var address)
                ? await router.HoldReasonAsync(address, ct)
                : InboundHoldReason.AddressRetired;
            message.HoldReason = hold;
            message.UpdatedAt = tenants.Now();
            if (hold is not null)
            {
                continue;
            }

            released++;
            var parsed = await db.BackgroundJobs.AnyAsync(j => j.Type == BackgroundJobTypes.ParseInboundMessage && j.TargetId == message.Id, ct)
                || await db.MessageParseRuns.AnyAsync(r => r.InboundMessageId == message.Id, ct);
            if (!parsed && message is { RoutingState: InboundRoutingState.Routed, ContentState: InboundContentState.Available }
                && message.SenderClass is null or InboundSenderClass.Bank or InboundSenderClass.TenantForward)
            {
                await jobs.QueueNextAsync(BackgroundJobTypes.ParseInboundMessage, message.Id, requestedBy, ct);
                queued++;
            }
        }

        return (released, queued);
    }

    /// <summary>The physical account's row, created on its first registration (concurrent first registrations insert one row).</summary>
    private async Task<Guid> EnsureAccountAsync(CzechBankAccount account, DateTimeOffset now, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO bank_accounts (id, iban, account_number, bank_code, currency, created_at)
            VALUES ({Guid.CreateVersion7()}, {account.Iban}, {account.AccountNumber}, {account.BankCode}, {CzechBankAccount.Currency}, {now})
            ON CONFLICT (iban) DO NOTHING
            """, ct);
        return await db.BankAccounts.Where(a => a.Iban == account.Iban).Select(a => a.Id).SingleAsync(ct);
    }

    /// <summary>Changes the label of an active connection; nothing else about a connection can change.</summary>
    public async Task<ServiceResult<BankConnectionView>> UpdateLabelAsync(
        Guid callerTenantId, Guid? subtenantId, Guid connectionId, string? label, Actor actor, CancellationToken ct = default)
    {
        if (TenantRules.ValidateLabel(label) is { } labelError)
        {
            return ServiceError.Validation("invalid_request", "label", labelError);
        }

        var (owner, connection) = await FindForChangeAsync(callerTenantId, subtenantId, connectionId, ct);
        if (connection is null)
        {
            return ServiceError.NotFound();
        }

        if (CheckChangeable(owner!) is { } stateError)
        {
            return stateError;
        }

        if (connection.Status == BankConnectionStatus.Archived)
        {
            return Archived("An archived bank connection cannot be changed.");
        }

        var newLabel = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        if (newLabel == connection.Label)
        {
            return await ToViewAsync(connection, ct);
        }

        tenants.AddAudit(actor, owner!.Id, AuditActions.BankConnectionLabelChanged, new
        {
            ConnectionId = connection.Id,
            From = connection.Label,
            To = newLabel,
        });
        connection.Label = newLabel;
        connection.UpdatedAt = tenants.Now();
        var saved = await SaveAsync(connection, ct);
        return saved.Error is { } saveError ? saveError : await ToViewAsync(connection, ct);
    }

    /// <summary>
    /// Archives a connection, keeping its address, messages, and history. Feature 013: the account is
    /// released and may be registered again. Repeating it has no further effect.
    /// </summary>
    public async Task<ServiceResult<bool>> ArchiveAsync(
        Guid callerTenantId, Guid? subtenantId, Guid connectionId, Actor actor, CancellationToken ct = default)
    {
        var (owner, connection) = await FindForChangeAsync(callerTenantId, subtenantId, connectionId, ct);
        if (connection is null)
        {
            return ServiceError.NotFound();
        }

        if (connection.Status == BankConnectionStatus.Archived)
        {
            return true;
        }

        if (CheckChangeable(owner!) is { } stateError)
        {
            return stateError;
        }

        var now = tenants.Now();
        connection.Status = BankConnectionStatus.Archived;
        connection.ArchivedAt = now;
        connection.ArchivedReason = BankConnectionArchivedReason.User;
        connection.UpdatedAt = now;
        tenants.AddAudit(actor, owner!.Id, AuditActions.BankConnectionArchived, new { ConnectionId = connection.Id, Reason = "user" });
        return await SaveAsync(true, ct);
    }

    /// <summary>
    /// Assigns a notification address on the default provider instance, or returns the current one.
    /// A connection that has only a test address gets a live address once a live instance is the
    /// default: the test address is retired (kept and reserved) and the response reports the
    /// replacement. An address paused for foreign mail (Feature 008) is replaced the same way.
    /// Otherwise a live address is never replaced, downgraded, or moved to another instance by
    /// a configuration change. Retries and concurrent requests end with one address: the
    /// connection's row version and the one-active-address index make all but one assignment fail,
    /// and those then return the saved address. An address whose provider setup is pending or
    /// failed is retried on the same reservation and is not shown until it is ready.
    /// </summary>
    public async Task<ServiceResult<BankConnectionView>> AssignAddressAsync(
        Guid callerTenantId, Guid? subtenantId, Guid connectionId, Actor actor, CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            var (owner, connection) = await FindForChangeAsync(callerTenantId, subtenantId, connectionId, ct);
            if (connection is null)
            {
                return ServiceError.NotFound();
            }

            var current = await db.InboundAddresses.AsNoTracking()
                .SingleOrDefaultAsync(a => a.BankConnectionId == connection.Id && a.State == InboundAddressState.Active, ct);
            var target = providers.Default;
            var archived = connection.Status == BankConnectionStatus.Archived;
            if (current is not null && current.ProvisioningState != InboundProvisioningState.Ready)
            {
                if (archived)
                {
                    return await ToViewAsync(connection, ct) with { ReplacedStub = false };
                }

                if (CheckChangeable(owner!) is { } pendingStateError)
                {
                    return pendingStateError;
                }

                return await ProvisionAsync(connection, current, false, ct);
            }

            // A paused address (Feature 008) is replaced like a stub: the old one stays retired, reserved, and paused.
            var paused = current?.Processing == InboundAddressProcessing.PausedForeignMail;
            if (current is not null && !(paused && !archived)
                && (current.Environment == InboundEnvironment.Live || target.Environment == InboundEnvironment.Test || archived))
            {
                return await ToViewAsync(connection, ct) with { ReplacedStub = false };
            }

            if (archived)
            {
                return Archived("An archived bank connection cannot get a notification address.");
            }

            if (CheckChangeable(owner!) is { } stateError)
            {
                return stateError;
            }

            var provisioner = provisioners.Single(p => p.Kind == target.Kind);
            var now = tenants.Now();
            var ready = !provisioner.RequiresRemoteSetup;
            var address = new InboundAddress
            {
                Id = Guid.CreateVersion7(),
                TenantId = owner!.Id,
                BankConnectionId = connection.Id,
                Address = RandomAddress.Create(target.Domain),
                ProviderInstanceId = target.Id,
                ProviderKind = target.Kind,
                Environment = target.Environment,
                ProvisioningState = ready ? InboundProvisioningState.Ready : InboundProvisioningState.Pending,
                ReadyAt = ready ? now : null,
                State = InboundAddressState.Active,
                CreatedAt = now,
            };

            try
            {
                // Retiring the test address, reserving the new one, and the audit event are one transaction, or nothing.
                await using (var transaction = await db.Database.BeginTransactionAsync(ct))
                {
                    connection.UpdatedAt = now;
                    if (current is not null)
                    {
                        db.InboundAddresses.Attach(current);
                        current.State = InboundAddressState.Retired;
                        current.RetiredAt = now;
                    }

                    // The old address must be retired before the new one becomes the active one.
                    await db.SaveChangesAsync(ct);

                    db.InboundAddresses.Add(address);
                    tenants.AddAudit(actor, owner.Id,
                        current is null ? AuditActions.BankConnectionAddressAssigned : AuditActions.BankConnectionAddressReplaced, new
                        {
                            ConnectionId = connection.Id,
                            InboundAddress = address.Address,
                            ProviderInstanceId = address.ProviderInstanceId,
                            EmailProvider = address.ProviderKind,
                            Environment = TenantService.Lower(address.Environment),
                            RetiredAddress = current?.Address,
                        });

                    await db.SaveChangesAsync(ct);
                    await transaction.CommitAsync(ct);
                }

                return ready
                    ? await ToViewAsync(connection, ct) with { ReplacedStub = current is not null }
                    : await ProvisionAsync(connection, address, current is not null, ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another request changed the connection, most likely assigning an address; reread it.
                db.ChangeTracker.Clear();
            }
            catch (DbUpdateException ex) when (
                TenantService.IsUniqueViolation(ex, InboundAddressConfiguration.AddressUniqueIndex)
                || TenantService.IsUniqueViolation(ex, InboundAddressConfiguration.OneActivePerConnectionIndex))
            {
                // A random address collided with an existing one, or a concurrent request assigned one; try again.
                db.ChangeTracker.Clear();
            }

            if (attempt == AddressAttempts)
            {
                return ServiceError.Conflict("concurrent_modification", "The bank connection was changed concurrently; retry the request.");
            }
        }
    }

    /// <summary>Completes provider-side setup of a durable reservation; repeated calls retry the same address.</summary>
    private async Task<ServiceResult<BankConnectionView>> ProvisionAsync(
        BankConnection connection, InboundAddress reservation, bool replacedStub, CancellationToken ct)
    {
        var instance = providers.Find(reservation.ProviderInstanceId);
        var provisioner = instance is null ? null : provisioners.SingleOrDefault(p => p.Kind == instance.Kind);
        var result = provisioner is null
            ? new ProvisioningResult(false, $"Provider instance '{reservation.ProviderInstanceId}' is not configured on this server.")
            : await provisioner.EnsureAddressAsync(instance!, reservation.Address, ct);

        var tracked = await db.InboundAddresses.SingleAsync(a => a.Id == reservation.Id, ct);
        tracked.ProvisioningState = result.Ready ? InboundProvisioningState.Ready : InboundProvisioningState.Failed;
        tracked.ReadyAt = result.Ready ? tenants.Now() : null;
        tracked.ProvisioningError = result.Ready ? null : BackgroundJobQueue.Truncate(result.Error ?? "Provider setup failed.");
        await db.SaveChangesAsync(ct);
        return await ToViewAsync(connection, ct) with { ReplacedStub = replacedStub };
    }

    /// <summary>
    /// A connection that may receive new payments: it is active and its tenant is active. A
    /// suspended child stays visible to its parent but cannot be paid.
    /// </summary>
    public async Task<ServiceResult<BankConnection>> GetPayableAsync(
        Guid callerTenantId, Guid? subtenantId, Guid connectionId, CancellationToken ct = default)
    {
        if (await FindOwnerAsync(callerTenantId, subtenantId, ct) is not { } owner)
        {
            return ServiceError.NotFound();
        }

        var connection = await db.BankConnections.AsNoTracking()
            .SingleOrDefaultAsync(b => b.Id == connectionId && b.TenantId == owner.Id, ct);
        if (connection is null)
        {
            return ServiceError.NotFound();
        }

        return owner.Status switch
        {
            TenantStatus.Deleted => TenantService.TenantDeleted(owner, "The tenant is deleted and cannot receive payments."),
            TenantStatus.Suspended => ServiceError.Conflict("tenant_not_active", "The tenant is suspended and cannot receive payments."),
            _ when connection.Status == BankConnectionStatus.Archived =>
                Archived("An archived bank connection cannot be used for new payments."),
            _ => connection,
        };
    }

    private async Task<BankConnectionView> ToViewAsync(BankConnection connection, CancellationToken ct) =>
        (await ToViewsAsync([connection], ct))[0];

    /// <summary>Adds each connection's current address and reception facts, in the given order.</summary>
    private async Task<List<BankConnectionView>> ToViewsAsync(List<BankConnection> connections, CancellationToken ct)
    {
        var ids = connections.Select(c => c.Id).ToList();
        var addresses = await db.InboundAddresses.AsNoTracking()
            .Where(a => ids.Contains(a.BankConnectionId) && a.State == InboundAddressState.Active)
            .ToDictionaryAsync(a => a.BankConnectionId, ct);

        var received = await db.InboundMessages.AsNoTracking()
            .Where(m => m.BankConnectionId != null && ids.Contains(m.BankConnectionId.Value))
            .GroupBy(m => m.BankConnectionId!.Value)
            .Select(g => new
            {
                ConnectionId = g.Key,
                LastReceived = g.Max(m => (DateTimeOffset?)m.ReceivedAt),
                LastRetrieved = g.Max(m => m.ContentStoredAt),
            })
            .ToDictionaryAsync(r => r.ConnectionId, ct);

        // The latest retrieval error per connection; errors are cleared once content is retrieved.
        var errors = (await db.InboundMessages.AsNoTracking()
                .Where(m => m.FetchErrorAt != null && m.BankConnectionId != null && ids.Contains(m.BankConnectionId.Value))
                .Select(m => new { ConnectionId = m.BankConnectionId!.Value, m.FetchErrorAt, m.FetchErrorCode, m.FetchErrorMessage })
                .ToListAsync(ct))
            .GroupBy(m => m.ConnectionId)
            .ToDictionary(g => g.Key, g => g.MaxBy(m => m.FetchErrorAt)!);

        return connections.Select(c =>
        {
            var reception = ReceptionFacts.None;
            if (received.TryGetValue(c.Id, out var facts))
            {
                var error = errors.GetValueOrDefault(c.Id);
                reception = new ReceptionFacts(
                    facts.LastReceived, facts.LastRetrieved, error?.FetchErrorCode, error?.FetchErrorMessage, error?.FetchErrorAt);
            }

            return new BankConnectionView(c, addresses.GetValueOrDefault(c.Id), reception);
        }).ToList();
    }

    private Task<Tenant?> FindOwnerAsync(Guid callerTenantId, Guid? subtenantId, CancellationToken ct) =>
        subtenantId is { } childId
            ? tenants.FindSubtenantAsync(callerTenantId, childId, ct)
            : tenants.FindAsync(callerTenantId, ct);

    private async Task<(Tenant? Owner, BankConnection? Connection)> FindForChangeAsync(
        Guid callerTenantId, Guid? subtenantId, Guid connectionId, CancellationToken ct)
    {
        if (await FindOwnerAsync(callerTenantId, subtenantId, ct) is not { } owner)
        {
            return (null, null);
        }

        var connection = await db.BankConnections.SingleOrDefaultAsync(b => b.Id == connectionId && b.TenantId == owner.Id, ct);
        return (owner, connection);
    }

    /// <summary>
    /// A deleted tenant's connections are history; a suspended child may be inspected by its
    /// parent but not changed. A tenant's own blocked access is refused earlier, by authorization.
    /// </summary>
    private static ServiceError? CheckChangeable(Tenant owner) => owner.Status switch
    {
        TenantStatus.Deleted => TenantService.TenantDeleted(owner),
        TenantStatus.Suspended => ServiceError.Conflict("tenant_not_active", "The tenant is suspended; reactivate it before changing its bank settings."),
        _ => null,
    };

    /// <summary>
    /// Feature 013: only an active registration blocks an account; archived ones, in any tenant,
    /// deleted tenants included, do not. The caller's own active registration is named so it can be
    /// used; another tenant's is never revealed and gets the same generic error as any unavailable account.
    /// </summary>
    private async Task<ServiceError?> AccountTakenAsync(string iban, Guid ownerId, CancellationToken ct)
    {
        var active = await db.BankConnections.AsNoTracking()
            .Where(b => b.Iban == iban && b.Status == BankConnectionStatus.Active)
            .Select(b => new { b.Id, b.TenantId })
            .SingleOrDefaultAsync(ct);

        return active switch
        {
            null => null,
            _ when active.TenantId == ownerId => ServiceError.Conflict(
                "bank_connection_exists", "This tenant already has an active bank connection for this account.") with { BankConnectionId = active.Id },
            _ => AccountUnavailable(),
        };
    }

    private static ServiceError AccountUnavailable() =>
        ServiceError.Conflict("account_unavailable", "This bank account cannot be used.");

    private static ServiceError Archived(string message) => ServiceError.Conflict("bank_connection_archived", message);

    private async Task<ServiceResult<T>> SaveAsync<T>(T value, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return value;
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return ServiceError.Conflict("concurrent_modification", "The bank connection was changed concurrently; retry the request.");
        }
    }
}
