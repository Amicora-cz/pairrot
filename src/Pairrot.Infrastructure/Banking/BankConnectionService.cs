using Microsoft.EntityFrameworkCore;
using Pairrot.Core.Banking;
using Pairrot.Core.Tenants;
using Pairrot.Infrastructure.Persistence;
using Pairrot.Infrastructure.Tenants;

namespace Pairrot.Infrastructure.Banking;

public sealed record BankConnectionInput(string? AccountNumber, string? Iban, string? Currency, string? Label);

/// <summary>
/// Bank connections of one tenant. Every method takes the caller's tenant and, for parent
/// management, the ID of a direct child; the owner is resolved here and checked again together
/// with the connection on every operation. Callers are authorized by the API layer first.
/// </summary>
public class BankConnectionService(PairrotDbContext db, TenantService tenants, IInboundAddressProvider provider)
{
    private const int AddressAttempts = 3;

    public string ProviderName => provider.Name;

    public async Task<ServiceResult<IReadOnlyList<BankConnection>>> ListAsync(
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
        return await query.OrderBy(b => b.Id).ToListAsync(ct);
    }

    public async Task<ServiceResult<BankConnection>> GetAsync(
        Guid callerTenantId, Guid? subtenantId, Guid connectionId, CancellationToken ct = default)
    {
        if (await FindOwnerAsync(callerTenantId, subtenantId, ct) is not { } owner)
        {
            return ServiceError.NotFound();
        }

        var connection = await db.BankConnections.AsNoTracking()
            .SingleOrDefaultAsync(b => b.Id == connectionId && b.TenantId == owner.Id, ct);
        return connection is null ? ServiceError.NotFound() : connection;
    }

    public async Task<ServiceResult<BankConnection>> CreateAsync(
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

        var now = tenants.Now();
        var connection = new BankConnection
        {
            Id = Guid.CreateVersion7(),
            TenantId = owner.Id,
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
            connection.AccountNumber,
            connection.Label,
        });

        try
        {
            await db.SaveChangesAsync(ct);
            return connection;
        }
        catch (DbUpdateException ex) when (TenantService.IsUniqueViolation(ex, BankConnectionConfiguration.IbanUniqueIndex))
        {
            // A concurrent request saved the same account first.
            db.ChangeTracker.Clear();
            return await AccountTakenAsync(account.Iban, owner.Id, ct) ?? AccountUnavailable();
        }
    }

    /// <summary>Changes the label of an active connection; nothing else about a connection can change.</summary>
    public async Task<ServiceResult<BankConnection>> UpdateLabelAsync(
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
            return connection;
        }

        tenants.AddAudit(actor, owner!.Id, AuditActions.BankConnectionLabelChanged, new
        {
            ConnectionId = connection.Id,
            From = connection.Label,
            To = newLabel,
        });
        connection.Label = newLabel;
        connection.UpdatedAt = tenants.Now();
        return await SaveAsync(connection, ct);
    }

    /// <summary>Archives a connection, keeping its account and address reserved. Repeating it has no further effect.</summary>
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
        connection.UpdatedAt = now;
        tenants.AddAudit(actor, owner!.Id, AuditActions.BankConnectionArchived, new { ConnectionId = connection.Id });
        return await SaveAsync(true, ct);
    }

    /// <summary>
    /// Assigns a notification address, or returns the one already assigned. Retries and concurrent
    /// requests end with exactly one address: the row version makes all but one assignment fail,
    /// and those then return the saved address.
    /// </summary>
    public async Task<ServiceResult<BankConnection>> AssignAddressAsync(
        Guid callerTenantId, Guid? subtenantId, Guid connectionId, Actor actor, CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            var (owner, connection) = await FindForChangeAsync(callerTenantId, subtenantId, connectionId, ct);
            if (connection is null)
            {
                return ServiceError.NotFound();
            }

            if (connection.InboundAddress is not null)
            {
                return connection;
            }

            if (connection.Status == BankConnectionStatus.Archived)
            {
                return Archived("An archived bank connection cannot get a notification address.");
            }

            if (CheckChangeable(owner!) is { } stateError)
            {
                return stateError;
            }

            var now = tenants.Now();
            connection.InboundAddress = provider.CreateAddress();
            connection.EmailProvider = provider.Name;
            connection.AddressStatus = AddressStatus.StubCreated;
            connection.AddressCreatedAt = now;
            connection.UpdatedAt = now;
            tenants.AddAudit(actor, owner!.Id, AuditActions.BankConnectionAddressAssigned, new
            {
                ConnectionId = connection.Id,
                connection.InboundAddress,
                connection.EmailProvider,
            });

            try
            {
                // The address and its audit event are written in one transaction, or not at all.
                await db.SaveChangesAsync(ct);
                return connection;
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another request changed the connection, most likely assigning an address; reread it.
                db.ChangeTracker.Clear();
            }
            catch (DbUpdateException ex) when (TenantService.IsUniqueViolation(ex, BankConnectionConfiguration.AddressUniqueIndex))
            {
                // A random address collided with an existing one; generate another.
                db.ChangeTracker.Clear();
            }

            if (attempt == AddressAttempts)
            {
                return ServiceError.Conflict("concurrent_modification", "The bank connection was changed concurrently; retry the request.");
            }
        }
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
        TenantStatus.Deleted => ServiceError.Conflict("tenant_deleted", "The tenant is deleted and cannot be changed."),
        TenantStatus.Suspended => ServiceError.Conflict("tenant_not_active", "The tenant is suspended; reactivate it before changing its bank settings."),
        _ => null,
    };

    /// <summary>
    /// Every account can belong to one connection only, archived ones included. Another tenant's
    /// ownership is never revealed: it gets the same generic error as any unavailable account.
    /// </summary>
    private async Task<ServiceError?> AccountTakenAsync(string iban, Guid ownerId, CancellationToken ct)
    {
        var existing = await db.BankConnections.AsNoTracking()
            .Where(b => b.Iban == iban)
            .Select(b => new { b.TenantId, b.Status })
            .SingleOrDefaultAsync(ct);

        return existing switch
        {
            null => null,
            { Status: BankConnectionStatus.Active } when existing.TenantId == ownerId => ServiceError.Conflict(
                "duplicate_bank_connection", "This tenant already has a bank connection for this account."),
            { Status: BankConnectionStatus.Archived } when existing.TenantId == ownerId => ServiceError.Conflict(
                "duplicate_bank_connection", "This account belongs to an archived bank connection of this tenant and cannot be reused yet."),
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
