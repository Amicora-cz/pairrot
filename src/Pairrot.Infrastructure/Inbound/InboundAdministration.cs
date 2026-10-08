using Microsoft.EntityFrameworkCore;
using Pairrot.Core.Inbound;
using Pairrot.Core.Jobs;
using Pairrot.Core.Tenants;
using Pairrot.Infrastructure.Jobs;
using Pairrot.Infrastructure.Persistence;
using Pairrot.Infrastructure.Tenants;

namespace Pairrot.Infrastructure.Inbound;

/// <summary>Which messages an operator lists.</summary>
public enum InboundAttention
{
    /// <summary>Quarantined, failed, or held.</summary>
    Attention,
    Quarantined,
    Failed,
    Held,
    All,
}

public sealed record InboundOperatorView(
    InboundMessage Message,
    IReadOnlyList<InboundDeliveryReceipt> Receipts,
    IReadOnlyList<BackgroundJob> Jobs,
    InboundAddress? Address);

/// <summary>
/// Operator tooling (CLI only): the global view of received mail, including quarantined messages
/// no tenant can see. Routing can only be retried against the address map; there is no way to
/// assign a message to an arbitrary tenant.
/// </summary>
public class InboundAdministration(
    PairrotDbContext db, BackgroundJobQueue jobs, TenantService tenants, InboundRouter router, InboundFetchQueue fetches,
    SenderScreening screening)
{
    public async Task<IReadOnlyList<InboundMessage>> ListAsync(InboundAttention filter, int limit, CancellationToken ct = default)
    {
        var query = db.InboundMessages.AsNoTracking();
        query = filter switch
        {
            InboundAttention.Quarantined => query.Where(m => m.RoutingState != InboundRoutingState.Routed),
            InboundAttention.Failed => query.Where(m => m.ContentState == InboundContentState.Failed),
            InboundAttention.Held => query.Where(m => m.HoldReason != null),
            InboundAttention.Attention => query.Where(m => m.RoutingState != InboundRoutingState.Routed
                || m.ContentState == InboundContentState.Failed || m.HoldReason != null),
            _ => query,
        };

        return await query.OrderByDescending(m => m.Id).Take(limit).ToListAsync(ct);
    }

    public async Task<InboundOperatorView?> GetAsync(Guid messageId, CancellationToken ct = default)
    {
        var message = await db.InboundMessages.AsNoTracking().SingleOrDefaultAsync(m => m.Id == messageId, ct);
        if (message is null)
        {
            return null;
        }

        var receipts = await db.InboundDeliveryReceipts.AsNoTracking()
            .Where(r => r.InboundMessageId == messageId).OrderBy(r => r.AcceptedAt).ToListAsync(ct);
        var jobList = await db.BackgroundJobs.AsNoTracking()
            .Where(j => j.Type == BackgroundJobTypes.FetchInboundContent && j.TargetId == messageId)
            .OrderBy(j => j.Generation).ToListAsync(ct);
        var address = message.InboundAddressId is { } addressId
            ? await db.InboundAddresses.AsNoTracking().SingleOrDefaultAsync(a => a.Id == addressId, ct)
            : null;
        return new InboundOperatorView(message, receipts, jobList, address);
    }

    /// <summary>
    /// Routes a quarantined message again after the address map was fixed. A routed message keeps
    /// its owner: routing is never repeated or overridden.
    /// </summary>
    public async Task<ServiceResult<InboundMessage>> RetryRoutingAsync(Guid messageId, string operatorName, CancellationToken ct = default)
    {
        var message = await db.InboundMessages.SingleOrDefaultAsync(m => m.Id == messageId, ct);
        if (message is null)
        {
            return ServiceError.NotFound();
        }

        if (message.RoutingState == InboundRoutingState.Routed)
        {
            return ServiceError.Conflict("already_routed", "The message is already routed; its owner never changes.");
        }

        var route = await router.RouteAsync(message.EnvelopeRecipients, message.ProviderInstanceId, ct);
        message.RoutingState = route.State;
        message.UpdatedAt = jobs.Now();
        if (route.Address is { } address)
        {
            message.TenantId = address.TenantId;
            message.BankConnectionId = address.BankConnectionId;
            message.InboundAddressId = address.Id;
            message.HoldReason = route.Hold;

            // Content stored before routing must name the same owner, like content stored after it.
            if (message.ContentState == InboundContentState.Available && message.ContentRecipients is { } contentRecipients)
            {
                var evidence = new InboundContentSnapshot(null, null, null, contentRecipients, null, SenderAuthentication.Unknown);
                if (await router.ContradictsRouteAsync(evidence, address, ct))
                {
                    message.HoldReason = InboundHoldReason.RecipientMismatch;
                }
            }

            tenants.AddAudit(Actor.Administrator(operatorName), address.TenantId, AuditActions.InboundMessageRouted, new
            {
                MessageId = message.Id,
                ConnectionId = address.BankConnectionId,
                HoldReason = message.HoldReason is { } hold ? SnakeCaseEnum<InboundHoldReason>.ToName(hold) : null,
            });

            // Feature 014: an erased owner gets only the tombstone, like mail routed to it on arrival.
            var erased = await db.Tenants.AnyAsync(t => t.Id == address.TenantId && t.ErasureRequestedAt != null, ct);
            if (erased)
            {
                InboundContent.Minimize(message, InboundContentState.Erased, address.Address, jobs.Now());
                await Erasure.PersonalDataPurge.ReceiptsAsync(db, [message.Id], ct);
            }

            // Feature 008: the same collection rules as for mail that was routed on arrival.
            var collect = !erased && await screening.ApplyAsync(message, address, null, jobs.Now(), ct);
            var existing = await db.BackgroundJobs.AsNoTracking()
                .Where(j => j.Type == BackgroundJobTypes.FetchInboundContent && j.TargetId == message.Id)
                .ToListAsync(ct);
            if (collect && message.ContentState == InboundContentState.Pending && message.ContentMode == InboundContentMode.Deferred
                && !existing.Any(BackgroundJobQueue.IsActive))
            {
                jobs.Add(BackgroundJobTypes.FetchInboundContent, message.Id,
                    existing.Select(j => j.Generation).DefaultIfEmpty(0).Max() + 1, $"operator:{operatorName}");
            }
        }

        try
        {
            await db.SaveChangesAsync(ct);
            return message;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return ServiceError.Conflict("concurrent_modification", "The message changed concurrently; retry the command.");
        }
    }

    /// <summary>Queues retrieval for any message, quarantined ones included, e.g. after fixing provider credentials.</summary>
    public async Task<ServiceResult<QueuedRetrieval>> RetryFetchAsync(Guid messageId, string operatorName, CancellationToken ct = default)
    {
        if (!await db.InboundMessages.AnyAsync(m => m.Id == messageId, ct))
        {
            return ServiceError.NotFound();
        }

        return await fetches.QueueAsync(messageId, Actor.Administrator(operatorName), ct);
    }
}
