using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pairrot.Core.Inbound;
using Pairrot.Core.Matching;
using Pairrot.Core.Tenants;
using Pairrot.Core.Webhooks;
using Pairrot.Infrastructure.Jobs;
using Pairrot.Infrastructure.Matching;
using Pairrot.Infrastructure.Persistence;
using Pairrot.Infrastructure.Tenants;

namespace Pairrot.Infrastructure.Webhooks;

/// <summary>
/// Who manages a webhook: the caller's tenant, or (with <paramref name="SubtenantId"/>) a direct
/// child's own endpoint. <paramref name="ManagesChildren"/> is the current management authority
/// (a management key, or a session of a parent administrator); without it nothing about children
/// is configured or readable.
/// </summary>
public sealed record WebhookCaller(Guid CallerTenantId, Guid? SubtenantId, bool ManagesChildren, Actor Actor);

public sealed record DeliveryCounts(int Pending, int RetryWait, int Running, int Failed, int Held, int Delivered, int Abandoned);

public sealed record EndpointView(WebhookEndpoint Endpoint, IReadOnlyList<WebhookSigningKey> Keys, DeliveryCounts Counts);

/// <param name="Secret">The <c>pwhsec_</c> secret, shown only in the first response.</param>
public sealed record SecretReveal(EndpointView Endpoint, string KeyId, string Secret);

/// <param name="CurrentUrl"><c>null</c> once the endpoint was erased (Feature 014).</param>
public sealed record DeliveryView(WebhookDelivery Delivery, WebhookEvent Event, IReadOnlyList<WebhookDeliveryAttempt> Attempts, string? CurrentUrl);

public sealed record EventFilter(Guid? OwnerTenantId = null, Guid? PaymentRequestId = null, DateTimeOffset? From = null, DateTimeOffset? To = null);

public sealed record EndpointChange(Optional<string?> Url = default, Optional<WebhookScope> Scope = default, Optional<WebhookEndpointState> State = default);

/// <summary>
/// Feature 010's management API: one endpoint per tenant, signing keys, test probes, event and
/// delivery history, and explicit retry, replay, and abandonment. Changes are audited; nothing is
/// hard-deleted; no action changes a payment.
/// </summary>
public class WebhookService(
    PairrotDbContext db, TenantService tenants, BackgroundJobQueue jobs, WebhookKeyRing keyRing, ManualCommands commands,
    IOptions<WebhookOptions> options)
{
    public const int MaxReplayEvents = 100;
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromHours(24);
    public static readonly TimeSpan MaxGrace = TimeSpan.FromDays(7);

    public DateTimeOffset Now() => jobs.Now();

    public async Task<ServiceResult<CommandOutcome>> CreateAsync(
        WebhookCaller caller, string? url, WebhookScope scope, InboundEnvironment environment, CommandRequest command,
        Func<EndpointView, object> render, Func<SecretReveal, object> renderSecret, CancellationToken ct = default)
    {
        if (WebhookDestinations.Validate(url, options.Value.AllowedLocal) is { } urlError)
        {
            return ServiceError.Validation("invalid_url", "url", urlError);
        }

        if (await FindOwnerAsync(caller, ct) is not { } owner)
        {
            return ServiceError.NotFound();
        }

        if (ScopeError(caller, owner, scope) is { } scopeError)
        {
            return scopeError;
        }

        if (Inactive(owner) is { } inactive)
        {
            return inactive;
        }

        return await commands.RunAsync(owner.Id, owner.Id, command, async () =>
        {
            await WebhookRouter.LockExclusiveAsync(db, owner.Id, ct);
            if (await db.WebhookEndpoints.AnyAsync(e => e.TenantId == owner.Id, ct))
            {
                return ServiceError.Conflict("webhook_endpoint_exists", "The tenant already has a webhook endpoint; change it instead.");
            }

            var now = Now();
            var endpoint = new WebhookEndpoint
            {
                Id = Guid.CreateVersion7(),
                TenantId = owner.Id,
                Scope = scope,
                Environment = environment,
                Url = url!.Trim(),
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.WebhookEndpoints.Add(endpoint);
            db.WebhookDestinationRevisions.Add(NewRevision(endpoint, caller.Actor, now));
            var (keyId, secret) = AddKey(endpoint, now);
            tenants.AddAudit(caller.Actor, owner.Id, AuditActions.WebhookEndpointCreated, new
            {
                EndpointId = endpoint.Id,
                endpoint.Url,
                Scope = Snake(scope),
                Environment = Snake(environment),
                KeyId = keyId,
            });

            await db.SaveChangesAsync(ct);
            var view = await ViewAsync(endpoint, caller, owner, ct);
            return new CommandResult(201, render(view), new { EndpointId = endpoint.Id, KeyId = keyId })
            {
                FirstResponse = renderSecret(new SecretReveal(view, keyId, WebhookSignature.FormatSecret(secret))),
            };
        }, ct);
    }

    public async Task<ServiceResult<EndpointView>> GetAsync(WebhookCaller caller, CancellationToken ct = default)
    {
        if (await FindEndpointAsync(caller, ct) is not ({ } owner, { } endpoint))
        {
            return ServiceError.NotFound();
        }

        return await ViewAsync(endpoint, caller, owner, ct);
    }

    /// <summary>
    /// Changes the URL, scope, or state at <paramref name="expectedVersion"/>. A new URL is a new
    /// destination revision: queued work for the old one is held as <c>destination_changed</c> and is
    /// sent to the new host only by an explicit replay. Narrowing the scope holds children's work.
    /// </summary>
    public async Task<ServiceResult<EndpointView>> UpdateAsync(
        WebhookCaller caller, int expectedVersion, EndpointChange change, CancellationToken ct = default)
    {
        if (change.Url.HasValue && WebhookDestinations.Validate(change.Url.Value, options.Value.AllowedLocal) is { } urlError)
        {
            return ServiceError.Validation("invalid_url", "url", urlError);
        }

        if (await FindEndpointAsync(caller, ct) is not ({ } owner, { } found))
        {
            return ServiceError.NotFound();
        }

        if ((ManageError(caller, found) ?? (change.Scope.HasValue ? ScopeError(caller, owner, change.Scope.Value) : null)) is { } denied)
        {
            return denied;
        }

        if (Inactive(owner) is { } inactive)
        {
            return inactive;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await WebhookRouter.LockExclusiveAsync(db, owner.Id, ct);
        var endpoint = await db.WebhookEndpoints.SingleAsync(e => e.Id == found.Id, ct);
        if (endpoint.Version != expectedVersion)
        {
            return Stale();
        }

        var now = Now();
        var changes = new Dictionary<string, object?>();
        if (change.Url.HasValue && change.Url.Value!.Trim() is var url && url != endpoint.Url)
        {
            changes["url"] = new { From = endpoint.Url, To = url, Revision = endpoint.DestinationRevision + 1 };
            endpoint.Url = url;
            endpoint.DestinationRevision++;
            db.WebhookDestinationRevisions.Add(NewRevision(endpoint, caller.Actor, now));
            await HoldQueuedAsync(endpoint.Id, d => d.DestinationRevision != endpoint.DestinationRevision, WebhookHolds.DestinationChanged, now, ct);
        }

        if (change.Scope.HasValue && change.Scope.Value != endpoint.Scope)
        {
            changes["scope"] = new { From = Snake(endpoint.Scope), To = Snake(change.Scope.Value) };
            endpoint.Scope = change.Scope.Value;
            if (endpoint.Scope == WebhookScope.Own)
            {
                await HoldQueuedAsync(endpoint.Id, d => d.OwnerTenantId != endpoint.TenantId, WebhookHolds.NotAuthorized, now, ct);
            }
        }

        if (change.State.HasValue && change.State.Value != endpoint.State)
        {
            changes["state"] = new { From = Snake(endpoint.State), To = Snake(change.State.Value) };
            endpoint.State = change.State.Value;
        }

        if (changes.Count > 0)
        {
            endpoint.Version++;
            endpoint.UpdatedAt = now;
            tenants.AddAudit(caller.Actor, owner.Id, AuditActions.WebhookEndpointUpdated, new { EndpointId = endpoint.Id, Changes = changes });
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return await ViewAsync(endpoint, caller, owner, ct);
    }

    /// <summary>
    /// A new current key, revealed once. The previous key keeps signing for the grace period (zero
    /// revokes it at once). Only one retiring key may exist; it must expire or be revoked first.
    /// </summary>
    public async Task<ServiceResult<CommandOutcome>> RotateAsync(
        WebhookCaller caller, int? expectedVersion, TimeSpan? grace, bool revokeRetiring, CommandRequest command,
        Func<EndpointView, object> render, Func<SecretReveal, object> renderSecret, CancellationToken ct = default)
    {
        var period = grace ?? DefaultGrace;
        if (period < TimeSpan.Zero || period > MaxGrace)
        {
            return ServiceError.Validation("invalid_grace_period", "grace_period_seconds", "grace_period_seconds must be between 0 and 604800 (7 days).");
        }

        if (await FindEndpointAsync(caller, ct) is not ({ } owner, { } found))
        {
            return ServiceError.NotFound();
        }

        if ((ManageError(caller, found) ?? Inactive(owner)) is { } denied)
        {
            return denied;
        }

        if (expectedVersion is not { } version)
        {
            return ServiceError.PreconditionRequired("Send expected_version: the endpoint's version as shown to you.");
        }

        return await commands.RunAsync(owner.Id, owner.Id, command, async () =>
        {
            await WebhookRouter.LockExclusiveAsync(db, owner.Id, ct);
            var endpoint = await db.WebhookEndpoints.SingleAsync(e => e.Id == found.Id, ct);
            if (endpoint.Version != version)
            {
                return Stale();
            }

            var now = Now();
            var active = await db.WebhookSigningKeys
                .Where(k => k.EndpointId == endpoint.Id && k.State != WebhookKeyState.Retired).ToListAsync(ct);
            var previousKeyId = active.FirstOrDefault(k => k.State == WebhookKeyState.Current)?.KeyId;
            if (active.FirstOrDefault(k => k.State == WebhookKeyState.Retiring && k.RetiresAt > now) is { } retiring)
            {
                if (!revokeRetiring)
                {
                    return ServiceError.Conflict("retiring_key_exists",
                        "The previous key is still in its grace period; wait for it to expire or set revoke_retiring.");
                }

                Retire(retiring, now);
            }

            foreach (var expired in active.Where(k => k.State == WebhookKeyState.Retiring && k.RetiresAt <= now))
            {
                Retire(expired, now);
            }

            // Release the unique slot before the old key becomes the retiring one.
            await db.SaveChangesAsync(ct);
            if (active.FirstOrDefault(k => k.State == WebhookKeyState.Current) is { } current)
            {
                if (period == TimeSpan.Zero)
                {
                    Retire(current, now);
                }
                else
                {
                    current.State = WebhookKeyState.Retiring;
                    current.RetiresAt = now + period;
                }

                await db.SaveChangesAsync(ct);
            }

            var (keyId, secret) = AddKey(endpoint, now);
            endpoint.Version++;
            endpoint.UpdatedAt = now;
            tenants.AddAudit(caller.Actor, owner.Id, AuditActions.WebhookSecretRotated, new
            {
                EndpointId = endpoint.Id,
                KeyId = keyId,
                PreviousKeyId = previousKeyId,
                GracePeriodSeconds = (long)period.TotalSeconds,
            });

            await db.SaveChangesAsync(ct);
            var view = await ViewAsync(endpoint, caller, owner, ct);
            return new CommandResult(200, render(view), new { EndpointId = endpoint.Id, KeyId = keyId })
            {
                FirstResponse = renderSecret(new SecretReveal(view, keyId, WebhookSignature.FormatSecret(secret))),
            };
        }, ct);
    }

    /// <summary>Queues a signed synthetic <c>webhook.test</c> to the current destination, also while paused. No payment effect.</summary>
    public async Task<ServiceResult<CommandOutcome>> TestAsync(
        WebhookCaller caller, CommandRequest command, Func<WebhookDelivery, object> render, CancellationToken ct = default)
    {
        if (await FindEndpointAsync(caller, ct) is not ({ } owner, { } found))
        {
            return ServiceError.NotFound();
        }

        if ((ManageError(caller, found) ?? Inactive(owner)) is { } denied)
        {
            return denied;
        }

        return await commands.RunAsync(owner.Id, owner.Id, command, async () =>
        {
            await WebhookRouter.LockSharedAsync(db, owner.Id, ct);
            var endpoint = await db.WebhookEndpoints.AsNoTracking().SingleAsync(e => e.Id == found.Id, ct);
            var now = Now();
            var eventId = Guid.CreateVersion7();
            var body = WebhookEvents.TestBody(eventId, owner.Id, now);
            db.WebhookEvents.Add(new WebhookEvent
            {
                Id = eventId,
                Type = WebhookEvents.Test,
                TenantId = owner.Id,
                Environment = InboundEnvironment.Test,
                Synthetic = true,
                Body = body,
                BodySha256 = WebhookEvents.Sha256(body),
                CreatedAt = now,
            });
            var delivery = WebhookRouter.NewDelivery(eventId, endpoint, owner.Id, now);
            db.WebhookDeliveries.Add(delivery);
            await db.SaveChangesAsync(ct);
            return new CommandResult(202, render(delivery), new { DeliveryId = delivery.Id, EventId = eventId });
        }, ct);
    }

    /// <summary>Events the caller may see: its own and, with management authority, its direct children's.</summary>
    public async Task<ServiceResult<Page<WebhookEvent>>> ListEventsAsync(
        WebhookCaller caller, EventFilter filter, int limit, Guid? before, CancellationToken ct = default)
    {
        if (await FindOwnerAsync(caller, ct) is not { } owner)
        {
            return ServiceError.NotFound();
        }

        var children = caller.ManagesChildren && owner.CanCreateSubtenants && caller.SubtenantId is null;
        var query = db.WebhookEvents.AsNoTracking().Where(e => e.TenantId == owner.Id
            || (children && !e.Synthetic && db.Tenants.Any(t => t.Id == e.TenantId && t.ParentTenantId == owner.Id)));
        if (filter.OwnerTenantId is { } ownerId)
        {
            query = query.Where(e => e.TenantId == ownerId);
        }

        if (filter.PaymentRequestId is { } requestId)
        {
            query = query.Where(e => e.PaymentRequestId == requestId);
        }

        if (filter.From is { } from)
        {
            query = query.Where(e => e.CreatedAt >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(e => e.CreatedAt < to);
        }

        if (before is { } cursor)
        {
            query = query.Where(e => e.Id.CompareTo(cursor) < 0);
        }

        var items = await query.OrderByDescending(e => e.Id).Take(limit + 1).ToListAsync(ct);
        var hasMore = items.Count > limit;
        if (hasMore)
        {
            items.RemoveAt(items.Count - 1);
        }

        return new Page<WebhookEvent>(items, hasMore ? items[^1].Id : null);
    }

    public async Task<ServiceResult<Page<WebhookDelivery>>> ListDeliveriesAsync(
        WebhookCaller caller, WebhookDeliveryState? state, int limit, Guid? before, CancellationToken ct = default)
    {
        if (await FindEndpointAsync(caller, ct) is not ({ } owner, { } endpoint))
        {
            return ServiceError.NotFound();
        }

        var query = Visible(db.WebhookDeliveries.AsNoTracking().Where(d => d.EndpointId == endpoint.Id), caller, owner, endpoint);
        if (state is { } value)
        {
            query = query.Where(d => d.State == value);
        }

        if (before is { } cursor)
        {
            query = query.Where(d => d.Id.CompareTo(cursor) < 0);
        }

        var items = await query.OrderByDescending(d => d.Id).Take(limit + 1).ToListAsync(ct);
        var hasMore = items.Count > limit;
        if (hasMore)
        {
            items.RemoveAt(items.Count - 1);
        }

        return new Page<WebhookDelivery>(items, hasMore ? items[^1].Id : null);
    }

    public async Task<ServiceResult<DeliveryView>> GetDeliveryAsync(WebhookCaller caller, Guid deliveryId, CancellationToken ct = default)
    {
        if (await FindDeliveryAsync(caller, deliveryId, ct) is not ({ } endpoint, { } delivery))
        {
            return ServiceError.NotFound();
        }

        return await DeliveryViewAsync(delivery, endpoint, ct);
    }

    /// <summary>
    /// Starts a new generation (four attempts) for failed or held work whose cause is resolved. Work
    /// already pending, running, or waiting is returned unchanged; a delivered event needs a replay.
    /// </summary>
    public async Task<ServiceResult<CommandOutcome>> RetryAsync(
        WebhookCaller caller, Guid deliveryId, int? expectedVersion, string? reason, CommandRequest command,
        Func<DeliveryView, object> render, CancellationToken ct = default) =>
        await DeliveryCommandAsync(caller, deliveryId, expectedVersion, reason, command, render, async (delivery, endpoint, text) =>
        {
            var ev = await db.WebhookEvents.AsNoTracking().SingleAsync(e => e.Id == delivery.EventId, ct);
            if (ev.Body is null)
            {
                return (PayloadErased(), false);
            }

            switch (delivery.State)
            {
                case WebhookDeliveryState.Pending or WebhookDeliveryState.Running or WebhookDeliveryState.RetryWait:
                    return (null, false);
                case WebhookDeliveryState.Delivered:
                    return (ServiceError.Conflict("delivery_delivered", "The event was delivered; use replay to send it again."), false);
                case WebhookDeliveryState.Abandoned:
                    return (ServiceError.Conflict("delivery_abandoned", "The delivery was abandoned; use replay to send the event again."), false);
            }

            if (delivery.DestinationRevision != endpoint.DestinationRevision)
            {
                return (ServiceError.Conflict(WebhookHolds.DestinationChanged,
                    "The endpoint's URL changed since this delivery was queued; replay the event to the current destination."), false);
            }

            if (await WebhookDeliveryWorker.HoldReasonAsync(db, delivery, endpoint, ev, ct) is { } hold)
            {
                return (ServiceError.Conflict(hold, $"The delivery cannot be sent now ({hold})."), false);
            }

            StartGeneration(delivery, endpoint.DestinationRevision);
            tenants.AddAudit(caller.Actor, endpoint.TenantId, AuditActions.WebhookDeliveryRetried,
                new { DeliveryId = delivery.Id, delivery.EventId, delivery.Generation, Reason = text });
            return (null, true);
        }, ct);

    /// <summary>Stops future attempts and keeps the history. A request already sent cannot be recalled.</summary>
    public async Task<ServiceResult<CommandOutcome>> AbandonAsync(
        WebhookCaller caller, Guid deliveryId, int? expectedVersion, string? reason, CommandRequest command,
        Func<DeliveryView, object> render, CancellationToken ct = default) =>
        await DeliveryCommandAsync(caller, deliveryId, expectedVersion, reason, command, render, (delivery, endpoint, text) =>
        {
            if (delivery.State is WebhookDeliveryState.Delivered or WebhookDeliveryState.Abandoned)
            {
                return Task.FromResult<(ServiceError?, bool)>((ServiceError.Conflict("delivery_finished", "The delivery is already delivered or abandoned."), false));
            }

            delivery.State = WebhookDeliveryState.Abandoned;
            delivery.HoldReason = null;
            delivery.DueAt = null;
            delivery.LeaseToken = null;
            delivery.LeaseOwner = null;
            delivery.LeaseExpiresAt = null;
            delivery.AbandonReason = text;
            delivery.LastOutcome = "abandoned";
            tenants.AddAudit(caller.Actor, endpoint.TenantId, AuditActions.WebhookDeliveryAbandoned,
                new { DeliveryId = delivery.Id, delivery.EventId, Reason = text });
            return Task.FromResult<(ServiceError?, bool)>((null, true));
        }, ct);

    /// <summary>
    /// Explicit replay of up to 100 events to the current destination revision. Every event must be
    /// in the endpoint's current scope and environment; the batch is rejected as a whole otherwise.
    /// Existing deliveries keep their ID and body and get a new generation; new ones are created.
    /// </summary>
    public async Task<ServiceResult<CommandOutcome>> ReplayAsync(
        WebhookCaller caller, IReadOnlyList<Guid> eventIds, int? expectedRevision, string? reason, CommandRequest command,
        Func<IReadOnlyList<WebhookDelivery>, object> render, CancellationToken ct = default)
    {
        if (MatchingRules.ValidateReason(reason) is { } reasonError)
        {
            return ServiceError.Validation("invalid_reason", "reason", reasonError);
        }

        if (eventIds.Count is 0 or > MaxReplayEvents || eventIds.Distinct().Count() != eventIds.Count)
        {
            return ServiceError.Validation("invalid_event_ids", "event_ids", $"event_ids must list 1 to {MaxReplayEvents} distinct event IDs.");
        }

        if (await FindEndpointAsync(caller, ct) is not ({ } owner, { } found))
        {
            return ServiceError.NotFound();
        }

        if ((ManageError(caller, found) ?? Inactive(owner)) is { } denied)
        {
            return denied;
        }

        if (expectedRevision is not { } revision)
        {
            return ServiceError.PreconditionRequired("Send expected_destination_revision: the destination you intend to send to.");
        }

        return await commands.RunAsync(owner.Id, owner.Id, command, async () =>
        {
            await WebhookRouter.LockExclusiveAsync(db, owner.Id, ct);
            var endpoint = await db.WebhookEndpoints.AsNoTracking().SingleAsync(e => e.Id == found.Id, ct);
            if (endpoint.DestinationRevision != revision)
            {
                return ServiceError.PreconditionFailed("The endpoint's destination changed; check the new URL and replay to its revision.");
            }

            var events = await db.WebhookEvents.AsNoTracking().Where(e => eventIds.Contains(e.Id)).ToListAsync(ct);
            var children = endpoint.Scope == WebhookScope.OwnAndDirectChildren && caller.ManagesChildren && owner.CanCreateSubtenants
                ? await db.Tenants.AsNoTracking().Where(t => t.ParentTenantId == owner.Id).Select(t => t.Id).ToListAsync(ct)
                : [];
            if (events.Count != eventIds.Count || events.Any(e => e.Synthetic
                    || (e.TenantId != owner.Id && !children.Contains(e.TenantId))))
            {
                return ServiceError.NotFound("One or more events were not found in this endpoint's scope; nothing was queued.");
            }

            if (events.Any(e => e.Body is null))
            {
                return PayloadErased();
            }

            if (events.Any(e => e.Environment != endpoint.Environment))
            {
                return ServiceError.Conflict(WebhookHolds.EnvironmentMismatch, "Test payment changes go only to test endpoints, live ones only to live endpoints.");
            }

            var existing = await db.WebhookDeliveries.Where(d => d.EndpointId == endpoint.Id && eventIds.Contains(d.EventId)).ToListAsync(ct);
            if (existing.Any(d => d.State == WebhookDeliveryState.Running))
            {
                return ServiceError.Conflict("delivery_in_progress", "An attempt is in flight for one of the events; replay after it completes.");
            }

            var now = Now();
            var result = new List<WebhookDelivery>();
            foreach (var ev in events.OrderBy(e => e.CreatedAt))
            {
                if (existing.FirstOrDefault(d => d.EventId == ev.Id) is { } delivery)
                {
                    StartGeneration(delivery, endpoint.DestinationRevision);
                    delivery.UpdatedAt = now;
                    delivery.Version++;
                }
                else
                {
                    delivery = WebhookRouter.NewDelivery(ev.Id, endpoint, ev.TenantId, now);
                    db.WebhookDeliveries.Add(delivery);
                }

                result.Add(delivery);
            }

            tenants.AddAudit(caller.Actor, owner.Id, AuditActions.WebhookEventsReplayed, new
            {
                EndpointId = endpoint.Id,
                endpoint.DestinationRevision,
                endpoint.Url,
                EventIds = eventIds,
                Reason = reason!.Trim(),
            });
            await db.SaveChangesAsync(ct);
            return new CommandResult(202, render(result), new { DeliveryIds = result.Select(d => d.Id) });
        }, ct);
    }

    /// <summary>For the CLI backfill: events of the subscriber's scope in a time range without a delivery to its endpoint.</summary>
    public async Task<(WebhookEndpoint? Endpoint, IReadOnlyList<Guid> EventIds)> BackfillCandidatesAsync(
        Guid subscriberTenantId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var endpoint = await db.WebhookEndpoints.AsNoTracking().SingleOrDefaultAsync(e => e.TenantId == subscriberTenantId, ct);
        if (endpoint is null)
        {
            return (null, []);
        }

        var children = endpoint.Scope == WebhookScope.OwnAndDirectChildren;
        var ids = await db.WebhookEvents.AsNoTracking()
            .Where(e => !e.Synthetic && e.Environment == endpoint.Environment && e.CreatedAt >= from && e.CreatedAt < to
                && (e.TenantId == subscriberTenantId || (children && db.Tenants.Any(t => t.Id == e.TenantId && t.ParentTenantId == subscriberTenantId)))
                && !db.WebhookDeliveries.Any(d => d.EventId == e.Id && d.EndpointId == endpoint.Id))
            .OrderBy(e => e.CreatedAt)
            .Select(e => e.Id)
            .ToListAsync(ct);
        return (endpoint, ids);
    }

    /// <summary>Operator backfill: creates deliveries for the given events to the endpoint's current revision.</summary>
    public async Task<int> BackfillAsync(Guid endpointId, IReadOnlyList<Guid> eventIds, Actor actor, CancellationToken ct = default)
    {
        var created = 0;
        foreach (var chunk in eventIds.Chunk(MaxReplayEvents))
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var endpoint = await db.WebhookEndpoints.AsNoTracking().SingleAsync(e => e.Id == endpointId, ct);
            await WebhookRouter.LockExclusiveAsync(db, endpoint.TenantId, ct);
            var events = await db.WebhookEvents.AsNoTracking()
                .Where(e => chunk.Contains(e.Id) && !db.WebhookDeliveries.Any(d => d.EventId == e.Id && d.EndpointId == endpointId))
                .ToListAsync(ct);
            var now = Now();
            foreach (var ev in events)
            {
                db.WebhookDeliveries.Add(WebhookRouter.NewDelivery(ev.Id, endpoint, ev.TenantId, now));
            }

            tenants.AddAudit(actor, endpoint.TenantId, AuditActions.WebhookEventsReplayed, new
            {
                EndpointId = endpoint.Id,
                endpoint.DestinationRevision,
                EventIds = events.Select(e => e.Id),
                Reason = "operator backfill",
            });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            created += events.Count;
        }

        return created;
    }

    private async Task<ServiceResult<CommandOutcome>> DeliveryCommandAsync(
        WebhookCaller caller, Guid deliveryId, int? expectedVersion, string? reason, CommandRequest command, Func<DeliveryView, object> render,
        Func<WebhookDelivery, WebhookEndpoint, string, Task<(ServiceError? Error, bool Changed)>> change, CancellationToken ct)
    {
        if (MatchingRules.ValidateReason(reason) is { } reasonError)
        {
            return ServiceError.Validation("invalid_reason", "reason", reasonError);
        }

        if (await FindDeliveryAsync(caller, deliveryId, ct) is not ({ } found, { } foundDelivery))
        {
            return ServiceError.NotFound();
        }

        var owner = (await tenants.FindAsync(found.TenantId, ct))!;
        if ((ManageError(caller, found) ?? Inactive(owner)) is { } denied)
        {
            return denied;
        }

        if (expectedVersion is not { } version)
        {
            return ServiceError.PreconditionRequired("Send expected_version: the delivery's version as shown to you.");
        }

        return await commands.RunAsync(owner.Id, owner.Id, command, async () =>
        {
            var delivery = await db.WebhookDeliveries.SingleAsync(d => d.Id == foundDelivery.Id, ct);
            var endpoint = await db.WebhookEndpoints.AsNoTracking().SingleAsync(e => e.Id == delivery.EndpointId, ct);
            if (delivery.Version != version)
            {
                return Stale();
            }

            var (error, changed) = await change(delivery, endpoint, reason!.Trim());
            if (error is not null)
            {
                return error;
            }

            if (changed)
            {
                delivery.UpdatedAt = Now();
                delivery.Version++;
            }

            await db.SaveChangesAsync(ct);
            return new CommandResult(202, render(await DeliveryViewAsync(delivery, endpoint, ct)), new { DeliveryId = delivery.Id });
        }, ct);
    }

    private void StartGeneration(WebhookDelivery delivery, int revision)
    {
        delivery.Generation++;
        delivery.AttemptInGeneration = 0;
        delivery.DestinationRevision = revision;
        delivery.State = WebhookDeliveryState.Pending;
        delivery.HoldReason = null;
        delivery.AbandonReason = null;
        delivery.DueAt = Now();
        delivery.LastOutcome = "queued";
    }

    private async Task HoldQueuedAsync(
        Guid endpointId, Func<WebhookDelivery, bool> applies, string reason, DateTimeOffset now, CancellationToken ct)
    {
        var queued = await db.WebhookDeliveries
            .Where(d => d.EndpointId == endpointId && (d.State == WebhookDeliveryState.Pending || d.State == WebhookDeliveryState.RetryWait))
            .ToListAsync(ct);
        foreach (var delivery in queued.Where(applies))
        {
            WebhookDeliveryWorker.Hold(delivery, reason, now);
        }
    }

    private (string KeyId, byte[] Secret) AddKey(WebhookEndpoint endpoint, DateTimeOffset now)
    {
        var (keyId, secret) = WebhookSignature.NewKey();
        db.WebhookSigningKeys.Add(new WebhookSigningKey
        {
            Id = Guid.CreateVersion7(),
            EndpointId = endpoint.Id,
            KeyId = keyId,
            ProtectedSecret = keyRing.Protect(secret),
            CreatedAt = now,
        });
        return (keyId, secret);
    }

    private static void Retire(WebhookSigningKey key, DateTimeOffset now)
    {
        key.State = WebhookKeyState.Retired;
        key.ProtectedSecret = null;
        key.RetiresAt ??= now;
        key.RetiredAt = now;
    }

    private static WebhookDestinationRevision NewRevision(WebhookEndpoint endpoint, Actor actor, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        EndpointId = endpoint.Id,
        Revision = endpoint.DestinationRevision,
        Url = endpoint.Url,
        CreatedBy = PaymentLedger.Label(actor),
        CreatedAt = now,
    };

    private async Task<EndpointView> ViewAsync(WebhookEndpoint endpoint, WebhookCaller caller, Tenant owner, CancellationToken ct)
    {
        var keys = await db.WebhookSigningKeys.AsNoTracking().Where(k => k.EndpointId == endpoint.Id).OrderBy(k => k.CreatedAt).ToListAsync(ct);
        var counts = await Visible(db.WebhookDeliveries.AsNoTracking().Where(d => d.EndpointId == endpoint.Id), caller, owner, endpoint)
            .GroupBy(d => d.State)
            .Select(g => new { State = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.State, g => g.Count, ct);
        int Count(WebhookDeliveryState state) => counts.GetValueOrDefault(state);
        return new EndpointView(endpoint, keys, new DeliveryCounts(
            Count(WebhookDeliveryState.Pending), Count(WebhookDeliveryState.RetryWait), Count(WebhookDeliveryState.Running),
            Count(WebhookDeliveryState.Failed), Count(WebhookDeliveryState.Held), Count(WebhookDeliveryState.Delivered),
            Count(WebhookDeliveryState.Abandoned)));
    }

    private async Task<DeliveryView> DeliveryViewAsync(WebhookDelivery delivery, WebhookEndpoint endpoint, CancellationToken ct)
    {
        var ev = await db.WebhookEvents.AsNoTracking().SingleAsync(e => e.Id == delivery.EventId, ct);
        var attempts = await db.WebhookDeliveryAttempts.AsNoTracking()
            .Where(a => a.DeliveryId == delivery.Id).OrderBy(a => a.Generation).ThenBy(a => a.AttemptNumber).ToListAsync(ct);
        return new DeliveryView(delivery, ev, attempts, endpoint.Url);
    }

    /// <summary>Children's deliveries only while the caller currently manages children and the endpoint includes them.</summary>
    private IQueryable<WebhookDelivery> Visible(IQueryable<WebhookDelivery> query, WebhookCaller caller, Tenant owner, WebhookEndpoint endpoint) =>
        caller.ManagesChildren && owner.CanCreateSubtenants && endpoint.Scope == WebhookScope.OwnAndDirectChildren && caller.SubtenantId is null
            ? query.Where(d => d.OwnerTenantId == owner.Id || db.Tenants.Any(t => t.Id == d.OwnerTenantId && t.ParentTenantId == owner.Id))
            : query.Where(d => d.OwnerTenantId == owner.Id);

    private async Task<(WebhookEndpoint? Endpoint, WebhookDelivery? Delivery)> FindDeliveryAsync(WebhookCaller caller, Guid deliveryId, CancellationToken ct)
    {
        if (await FindEndpointAsync(caller, ct) is not ({ } owner, { } endpoint))
        {
            return (null, null);
        }

        var delivery = await Visible(db.WebhookDeliveries.AsNoTracking().Where(d => d.Id == deliveryId && d.EndpointId == endpoint.Id), caller, owner, endpoint)
            .SingleOrDefaultAsync(ct);
        return (endpoint, delivery);
    }

    private async Task<Tenant?> FindOwnerAsync(WebhookCaller caller, CancellationToken ct) =>
        caller.SubtenantId is { } childId
            ? await tenants.FindSubtenantAsync(caller.CallerTenantId, childId, ct)
            : await tenants.FindAsync(caller.CallerTenantId, ct);

    private async Task<(Tenant? Owner, WebhookEndpoint? Endpoint)> FindEndpointAsync(WebhookCaller caller, CancellationToken ct)
    {
        if (await FindOwnerAsync(caller, ct) is not { } owner)
        {
            return (null, null);
        }

        return (owner, await db.WebhookEndpoints.AsNoTracking().SingleOrDefaultAsync(e => e.TenantId == owner.Id, ct));
    }

    /// <summary>A children scope needs management authority over the caller's own endpoint and the capability.</summary>
    private static ServiceError? ScopeError(WebhookCaller caller, Tenant owner, WebhookScope scope) =>
        scope == WebhookScope.OwnAndDirectChildren && (!caller.ManagesChildren || caller.SubtenantId is not null || !owner.CanCreateSubtenants)
            ? ServiceError.Forbidden("Only a management key or a parent administrator can subscribe to direct children's payments.")
            : null;

    private static ServiceError? ManageError(WebhookCaller caller, WebhookEndpoint endpoint) =>
        endpoint.Scope == WebhookScope.OwnAndDirectChildren && !caller.ManagesChildren
            ? ServiceError.Forbidden("This endpoint includes direct children; manage it with a management key or as a parent administrator.")
            : null;

    private static ServiceError? Inactive(Tenant owner) => owner.Status switch
    {
        TenantStatus.Deleted => TenantService.TenantDeleted(owner),
        TenantStatus.Suspended => ServiceError.Conflict("tenant_not_active", "The tenant is suspended; reactivate it first."),
        _ => null,
    };

    /// <summary>Feature 014: an erased owner's event is never rebuilt or sent again, also not to the parent.</summary>
    private static ServiceError PayloadErased() =>
        ServiceError.Conflict("event_payload_erased", "The event's payload was erased with its tenant; it cannot be sent again.");

    private static ServiceError Stale() => ServiceError.PreconditionFailed("The record has changed since it was read; reload it and try again.");

    internal static string Snake<TEnum>(TEnum value) where TEnum : struct, Enum =>
        System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());
}
