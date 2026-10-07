using Microsoft.EntityFrameworkCore;
using Pairrot.Core.Inbound;
using Pairrot.Core.Jobs;
using Pairrot.Core.Tenants;
using Pairrot.Infrastructure.Jobs;
using Pairrot.Infrastructure.Persistence;
using Pairrot.Infrastructure.Tenants;

namespace Pairrot.Infrastructure.Inbound;

/// <param name="HoldReason">A reason, or <c>null</c> with <see cref="OnlyWithoutHold"/> for messages that are not held.</param>
public sealed record InboundMessageFilter(
    Guid? BankConnectionId = null,
    InboundContentState? ContentState = null,
    InboundHoldReason? HoldReason = null,
    bool OnlyWithoutHold = false,
    DateTimeOffset? ReceivedAfter = null,
    DateTimeOffset? ReceivedBefore = null);

/// <summary>A message with its retrieval jobs, oldest first.</summary>
public sealed record InboundMessageDetail(InboundMessage Message, IReadOnlyList<BackgroundJob> Jobs);

public sealed record QueuedRetrieval(BackgroundJob Job, bool Created);

/// <summary>
/// Inbound messages as their owners see them: only messages routed to the caller's tenant or,
/// for parent management, to a direct child. Unrouted messages have no tenant and are never listed.
/// </summary>
public class InboundMessageService(PairrotDbContext db, TenantService tenants, InboundFetchQueue fetches)
{
    public async Task<ServiceResult<Page<InboundMessage>>> ListAsync(
        Guid callerTenantId, Guid? subtenantId, InboundMessageFilter filter, int limit, Guid? before, CancellationToken ct = default)
    {
        if (await FindOwnerAsync(callerTenantId, subtenantId, ct) is not { } owner)
        {
            return ServiceError.NotFound();
        }

        var query = db.InboundMessages.AsNoTracking().Where(m => m.TenantId == owner.Id).Where(Visible);
        if (filter.BankConnectionId is { } connectionId)
        {
            query = query.Where(m => m.BankConnectionId == connectionId);
        }

        if (filter.ContentState is { } state)
        {
            query = query.Where(m => m.ContentState == state);
        }

        if (filter.OnlyWithoutHold)
        {
            query = query.Where(m => m.HoldReason == null);
        }
        else if (filter.HoldReason is { } reason)
        {
            query = query.Where(m => m.HoldReason == reason);
        }

        if (filter.ReceivedAfter is { } after)
        {
            query = query.Where(m => m.ReceivedAt >= after);
        }

        if (filter.ReceivedBefore is { } beforeTime)
        {
            query = query.Where(m => m.ReceivedAt < beforeTime);
        }

        if (before is { } cursor)
        {
            query = query.Where(m => m.Id.CompareTo(cursor) < 0);
        }

        // IDs are UUIDv7: newest accepted first.
        var items = await query.OrderByDescending(m => m.Id).Take(limit + 1).ToListAsync(ct);
        var hasMore = items.Count > limit;
        if (hasMore)
        {
            items.RemoveAt(items.Count - 1);
        }

        return new Page<InboundMessage>(items, hasMore ? items[^1].Id : null);
    }

    public async Task<ServiceResult<InboundMessageDetail>> GetAsync(
        Guid callerTenantId, Guid? subtenantId, Guid messageId, CancellationToken ct = default)
    {
        var (_, message) = await FindAsync(callerTenantId, subtenantId, messageId, ct);
        if (message is null)
        {
            return ServiceError.NotFound();
        }

        return new InboundMessageDetail(message, await ListJobsAsync(message.Id, ct));
    }

    /// <summary>The stored source; no provider call is ever made to read it.</summary>
    public async Task<ServiceResult<InboundMessage>> GetSourceAsync(
        Guid callerTenantId, Guid? subtenantId, Guid messageId, CancellationToken ct = default)
    {
        var (_, message) = await FindAsync(callerTenantId, subtenantId, messageId, ct);
        if (message is null)
        {
            return ServiceError.NotFound();
        }

        if (message.ContentState != InboundContentState.Available)
        {
            return ServiceError.Conflict("content_not_available", "The source content has not been retrieved.");
        }

        if (message.HoldReason == InboundHoldReason.RecipientMismatch)
        {
            // The content names other recipients and might not be this tenant's; an operator decides.
            return ServiceError.Conflict("content_withheld", "The retrieved content names different recipients and is held for review.");
        }

        return message;
    }

    /// <summary>
    /// Queues retrieval again after it failed, or returns the retrieval already queued. The target
    /// tenant (and, for its own key or session, its parent, checked by authorization) must be active.
    /// </summary>
    public async Task<ServiceResult<QueuedRetrieval>> RetryFetchAsync(
        Guid callerTenantId, Guid? subtenantId, Guid messageId, Actor actor, CancellationToken ct = default)
    {
        var (owner, message) = await FindAsync(callerTenantId, subtenantId, messageId, ct);
        if (message is null)
        {
            return ServiceError.NotFound();
        }

        switch (owner!.Status)
        {
            case TenantStatus.Deleted:
                return ServiceError.Conflict("tenant_deleted", "The tenant is deleted; its messages are history only.");
            case TenantStatus.Suspended:
                return ServiceError.Conflict("tenant_not_active", "The tenant is suspended; reactivate it before retrying.");
        }

        return await fetches.QueueAsync(message.Id, actor, ct);
    }

    /// <summary>Foreign mail and mail to a paused address are only counted (Feature 008), never listed as messages.</summary>
    private static readonly System.Linq.Expressions.Expression<Func<InboundMessage, bool>> Visible = m =>
        m.SenderClass != InboundSenderClass.Foreign && m.HoldReason != InboundHoldReason.AddressPaused;

    private Task<List<BackgroundJob>> ListJobsAsync(Guid messageId, CancellationToken ct) =>
        db.BackgroundJobs.AsNoTracking()
            .Where(j => j.Type == BackgroundJobTypes.FetchInboundContent && j.TargetId == messageId)
            .OrderBy(j => j.Generation)
            .ToListAsync(ct);

    private Task<Tenant?> FindOwnerAsync(Guid callerTenantId, Guid? subtenantId, CancellationToken ct) =>
        subtenantId is { } childId
            ? tenants.FindSubtenantAsync(callerTenantId, childId, ct)
            : tenants.FindAsync(callerTenantId, ct);

    private async Task<(Tenant? Owner, InboundMessage? Message)> FindAsync(
        Guid callerTenantId, Guid? subtenantId, Guid messageId, CancellationToken ct)
    {
        if (await FindOwnerAsync(callerTenantId, subtenantId, ct) is not { } owner)
        {
            return (null, null);
        }

        // Foreign and unrouted messages look exactly like missing ones.
        var message = await db.InboundMessages.AsNoTracking().Where(Visible)
            .SingleOrDefaultAsync(m => m.Id == messageId && m.TenantId == owner.Id, ct);
        return (owner, message);
    }
}

/// <summary>
/// Queues content retrieval for tenants and operators alike: one active retrieval per message, a
/// new generation after the last one ended, and never for content that is stored or that the
/// message's source cannot retrieve.
/// </summary>
public class InboundFetchQueue(
    PairrotDbContext db,
    BackgroundJobQueue jobs,
    TenantService tenants,
    InboundProviderRegistry providers,
    IEnumerable<IInboundContentSource> sources)
{
    public async Task<ServiceResult<QueuedRetrieval>> QueueAsync(Guid messageId, Actor actor, CancellationToken ct)
    {
        var message = await db.InboundMessages.AsNoTracking()
            .Where(m => m.Id == messageId)
            .Select(m => new { m.ContentState, m.ContentMode, m.ProviderInstanceId })
            .SingleAsync(ct);
        if (message.ContentState == InboundContentState.Available)
        {
            return ServiceError.Conflict("content_already_available",
                "The content is already stored. Later processing uses the saved snapshot; it is never retrieved again.");
        }

        // An instance this process does not know (e.g. the admin CLI without provider settings) is
        // left to the worker, which fails the job visibly if the instance is really missing.
        var instance = providers.Find(message.ProviderInstanceId);
        if (message.ContentMode != InboundContentMode.Deferred || instance is not null && !sources.Any(s => s.Kind == instance.Kind))
        {
            return ServiceError.Conflict("content_fetch_not_supported",
                "This message's source delivers content inline or is not configured here; it cannot be retrieved again.");
        }

        var requestedBy = RequesterOf(actor);
        try
        {
            var (job, created) = await jobs.EnqueueAsync(BackgroundJobTypes.FetchInboundContent, messageId, requestedBy, async () =>
            {
                var tracked = await db.InboundMessages.SingleAsync(m => m.Id == messageId, ct);
                tracked.ContentState = InboundContentState.Pending;
                tracked.UpdatedAt = jobs.Now();
                if (tracked.TenantId is { } tenantId)
                {
                    tenants.AddAudit(actor, tenantId, AuditActions.InboundMessageFetchRetried, new
                    {
                        MessageId = messageId,
                        ConnectionId = tracked.BankConnectionId,
                    });
                }
            }, ct);
            return new QueuedRetrieval(job, created);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return ServiceError.Conflict("concurrent_modification", "The message changed concurrently; retry the request.");
        }
    }

    internal static string RequesterOf(Actor actor) => actor switch
    {
        { UserId: { } userId } => $"user:{userId}",
        { ApiKeyId: { } keyId } => $"api_key:{keyId}",
        _ => $"operator:{actor.Description}",
    };
}
