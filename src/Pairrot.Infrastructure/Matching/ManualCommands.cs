using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Pairrot.Core.Matching;
using Pairrot.Infrastructure.Persistence;
using Pairrot.Infrastructure.Tenants;

namespace Pairrot.Infrastructure.Matching;

/// <summary>A manual command as the caller sent it: its <c>Idempotency-Key</c> and canonical input.</summary>
/// <param name="Fingerprint">From <see cref="ManualCommands.Fingerprint"/> over the action, route, and body values.</param>
public sealed record CommandRequest(string IdempotencyKey, string Action, string Fingerprint, Actor Actor);

/// <summary>The response of a command, committed together with its receipt.</summary>
public sealed record CommandResult(int Status, object Response, object ResourceIds);

/// <param name="Replayed">An earlier execution with the same key and input was returned; nothing changed now.</param>
public sealed record CommandOutcome(int Status, string Body, bool Replayed);

/// <summary>
/// Runs manual matching and review commands exactly once per owner and <c>Idempotency-Key</c>.
/// The change, its receipt, and the response body are committed in one transaction under the
/// connection lock. A retry with the same input returns the stored response even if the records
/// changed since; the same key with different input conflicts. A failed command stores nothing,
/// so its key can be used again.
/// </summary>
public class ManualCommands(PairrotDbContext db, PaymentLedger ledger)
{
    public const int MaxKeyLength = 200;

    /// <summary>The API's JSON conventions, so a stored response reads exactly like a fresh one.</summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public static string Fingerprint(string action, params object?[] values) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new object?[] { "v1", action, values }))));

    /// <summary>
    /// Executes <paramref name="body"/> for <paramref name="ownerId"/> under the lock of
    /// <paramref name="connectionId"/>, or returns the stored outcome of the same command.
    /// <paramref name="body"/> changes tracked entities; it must not commit.
    /// </summary>
    public async Task<ServiceResult<CommandOutcome>> RunAsync(
        Guid ownerId, Guid connectionId, CommandRequest command, Func<Task<ServiceResult<CommandResult>>> body, CancellationToken ct)
    {
        if (await StoredAsync(ownerId, command, ct) is { } stored)
        {
            return stored;
        }

        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await ledger.LockConnectionAsync(connectionId, ct);
        try
        {
            var result = await body();
            if (result.Error is { } error)
            {
                await transaction.RollbackAsync(ct);
                db.ChangeTracker.Clear();
                return error;
            }

            var json = JsonSerializer.Serialize(result.Value!.Response, Json);
            db.CommandReceipts.Add(new ManualCommandReceipt
            {
                Id = Guid.CreateVersion7(),
                TenantId = ownerId,
                IdempotencyKey = command.IdempotencyKey,
                Action = command.Action,
                Fingerprint = command.Fingerprint,
                ResponseStatus = result.Value.Status,
                ResponseBody = json,
                ResourceIds = JsonSerializer.Serialize(result.Value.ResourceIds, Json),
                Actor = PaymentLedger.Label(command.Actor),
                CreatedAt = ledger.Now(),
            });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return new CommandOutcome(result.Value.Status, json, false);
        }
        catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException
            || TenantService.IsUniqueViolation(ex, PaymentChangeConfiguration.SequenceUniqueIndex))
        {
            // A request edit or cancellation, which does not take the connection lock, won.
            await transaction.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            return ServiceError.PreconditionFailed("A record changed while the command ran; reload and try again.");
        }
        catch (DbUpdateException ex) when (TenantService.IsUniqueViolation(ex, ManualCommandReceiptConfiguration.KeyUniqueIndex))
        {
            // A concurrent request with the same key committed first.
            await transaction.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            return await StoredAsync(ownerId, command, ct)
                ?? ServiceError.Conflict("idempotency_key_conflict", "The Idempotency-Key is in use by another request.");
        }
        catch (DbUpdateException ex) when (TenantService.IsUniqueViolation(ex, PaymentAllocationConfiguration.OneActiveIndex))
        {
            await transaction.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            return ServiceError.Conflict("transaction_already_allocated", "The transaction already has an active allocation.");
        }
    }

    private async Task<ServiceResult<CommandOutcome>?> StoredAsync(Guid ownerId, CommandRequest command, CancellationToken ct)
    {
        var receipt = await db.CommandReceipts.AsNoTracking()
            .SingleOrDefaultAsync(r => r.TenantId == ownerId && r.IdempotencyKey == command.IdempotencyKey, ct);
        if (receipt is null)
        {
            return null;
        }

        return receipt.Action == command.Action && receipt.Fingerprint == command.Fingerprint
            ? new CommandOutcome(receipt.ResponseStatus, receipt.ResponseBody, true)
            : ServiceError.Conflict("idempotency_key_conflict",
                "This Idempotency-Key was already used for a different command; use a new key for a new command.");
    }
}
