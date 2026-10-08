using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pairrot.Core.Tenants;
using Pairrot.Core.Webhooks;
using Pairrot.Infrastructure.Erasure;
using Pairrot.Infrastructure.Jobs;
using Pairrot.Infrastructure.Persistence;

namespace Pairrot.Infrastructure.Webhooks;

/// <summary>
/// The single owner of outgoing webhook attempts (Features 010 and 011). A delivery is claimed with
/// a database lease; authorization, destination, and keys are rechecked; the attempt is reserved
/// and committed before the HTTP request, which runs outside any transaction; the outcome, the
/// delivery state, and the next due time are committed together, only by the lease holder. Four
/// attempts per generation: immediately, then 5 s, 60 s, and 1 h after the preceding failure.
/// </summary>
public class WebhookDeliveryWorker(
    PairrotDbContext db,
    BackgroundJobQueue jobs,
    IWebhookTransport transport,
    WebhookKeyRing keys,
    IOptions<WebhookOptions> options,
    IServiceScopeFactory scopes,
    ILogger<WebhookDeliveryWorker> logger)
{
    private static readonly string ProcessOwner = $"{Environment.MachineName}:{Environment.ProcessId}";

    /// <summary>Hold reasons that clear by themselves once the cause is gone; others need an explicit replay.</summary>
    public static readonly HashSet<string> SoftHolds = [WebhookHolds.TenantNotActive, WebhookHolds.NotAuthorized, WebhookHolds.SigningKeyUnavailable];

    /// <summary>Recovers expired leases, then sends due deliveries until none is due; returns how many were processed.</summary>
    public async Task<int> RunDueAsync(CancellationToken ct = default)
    {
        await RecoverExpiredAsync(ct);
        var processed = 0;
        while (true)
        {
            var claimed = new List<(Guid Id, Guid Token)>();
            for (var i = 0; i < Math.Max(1, options.Value.MaxParallel); i++)
            {
                if (await ClaimAsync(ct) is not { } lease)
                {
                    break;
                }

                claimed.Add(lease);
            }

            if (claimed.Count == 0)
            {
                return processed;
            }

            await Task.WhenAll(claimed.Select(async lease =>
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<WebhookDeliveryWorker>().ProcessAsync(lease.Id, lease.Token, ct);
            }));
            processed += claimed.Count;
        }
    }

    /// <summary>
    /// Claims the oldest due delivery of an active endpoint (or a synthetic test) whose endpoint has
    /// nothing in flight. Claims are serialized, so the one-in-flight rule holds across workers.
    /// </summary>
    public async Task<(Guid Id, Guid Token)?> ClaimAsync(CancellationToken ct = default)
    {
        var now = jobs.Now();
        var token = Guid.NewGuid();
        var expires = now + options.Value.LeaseDuration;
        var owner = $"{ProcessOwner}:{Guid.NewGuid():N}";
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended('webhook_claim', 1010))", ct);
        var ids = await db.Database.SqlQuery<Guid>($"""
            UPDATE webhook_deliveries
            SET state = 'running', lease_token = {token}, lease_owner = {owner}, lease_expires_at = {expires}, updated_at = {now}
            WHERE id = (
                SELECT d.id FROM webhook_deliveries d
                JOIN webhook_endpoints e ON e.id = d.endpoint_id
                JOIN webhook_events ev ON ev.id = d.event_id
                WHERE d.state IN ('pending', 'retry_wait') AND d.due_at <= {now}
                  AND (e.state = 'active' OR ev.synthetic)
                  AND NOT EXISTS (SELECT 1 FROM webhook_deliveries r WHERE r.endpoint_id = d.endpoint_id AND r.state = 'running')
                ORDER BY d.due_at, ev.created_at, d.id
                LIMIT 1
                FOR UPDATE OF d SKIP LOCKED)
            RETURNING id AS "Value"
            """).ToListAsync(ct);
        await transaction.CommitAsync(ct);
        return ids is [var id] ? (id, token) : null;
    }

    public async Task ProcessAsync(Guid deliveryId, Guid token, CancellationToken ct)
    {
        try
        {
            if (await ReserveAsync(deliveryId, token, ct) is not { } send)
            {
                return;
            }

            var started = jobs.Now();
            var response = await transport.SendAsync(send.Request, ct);
            await CompleteAsync(deliveryId, token, send.AttemptId, started, response, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The lease expires and recovery records the attempt as outcome_unknown.
            logger.LogError(ex, "Webhook delivery {DeliveryId} failed unexpectedly.", deliveryId);
        }
    }

    private sealed record PreparedSend(Guid AttemptId, WebhookHttpRequest Request);

    /// <summary>Rechecks everything, then reserves one attempt of the budget and commits it before sending.</summary>
    private async Task<PreparedSend?> ReserveAsync(Guid deliveryId, Guid token, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var delivery = await db.WebhookDeliveries.SingleAsync(d => d.Id == deliveryId, ct);
        if (delivery.State != WebhookDeliveryState.Running || delivery.LeaseToken != token)
        {
            return null;
        }

        var endpoint = await db.WebhookEndpoints.AsNoTracking().SingleAsync(e => e.Id == delivery.EndpointId, ct);
        var ev = await db.WebhookEvents.AsNoTracking().SingleAsync(e => e.Id == delivery.EventId, ct);
        var now = jobs.Now();

        // Feature 014: nothing of an erased owner, or to an erased subscriber, is sent again.
        if (ev.Body is null || await DataBarrier.IsClosedAsync(db, delivery.OwnerTenantId, ct)
            || await DataBarrier.IsClosedAsync(db, endpoint.TenantId, ct))
        {
            Finish(delivery, WebhookDeliveryState.Abandoned, now, null, null, "abandoned");
            delivery.AbandonReason = ErasureRules.AbandonReason;
            await SaveAsync(transaction, ct);
            return null;
        }

        if (endpoint.State == WebhookEndpointState.Paused && !ev.Synthetic)
        {
            // Paused after the claim: back to the backlog without consuming an attempt.
            Release(delivery, WebhookDeliveryState.Pending, now);
            await SaveAsync(transaction, ct);
            return null;
        }

        var signing = await SigningKeysAsync(endpoint.Id, now, ct);
        var url = await db.WebhookDestinationRevisions.AsNoTracking()
            .Where(r => r.EndpointId == endpoint.Id && r.Revision == delivery.DestinationRevision).Select(r => r.Url).SingleAsync(ct);
        var hold = await HoldReasonAsync(db, delivery, endpoint, ev, ct)
            ?? (WebhookDestinations.Validate(url, options.Value.AllowedLocal) is not null ? WebhookHolds.DestinationNotAllowed : null)
            ?? (signing.Count == 0 ? WebhookHolds.SigningKeyUnavailable : null);
        if (hold is not null)
        {
            Hold(delivery, hold, now);
            await SaveAsync(transaction, ct);
            return null;
        }

        if (delivery.AttemptInGeneration >= WebhookRetryPolicy.MaxAttempts)
        {
            Finish(delivery, WebhookDeliveryState.Failed, now, "attempts_exhausted", "All automatic attempts of this generation are used.", null);
            await SaveAsync(transaction, ct);
            return null;
        }

        delivery.AttemptInGeneration++;
        delivery.TotalAttempts++;
        delivery.UpdatedAt = now;
        var timestamp = now.ToUnixTimeSeconds();
        var attempt = new WebhookDeliveryAttempt
        {
            Id = Guid.CreateVersion7(),
            DeliveryId = delivery.Id,
            Generation = delivery.Generation,
            AttemptNumber = delivery.AttemptInGeneration,
            DestinationRevision = delivery.DestinationRevision,
            Url = url!,
            KeyIds = string.Join(' ', signing.Select(k => k.KeyId)),
            SignedTimestamp = timestamp,
            StartedAt = now,
            LeaseToken = token,
        };
        db.WebhookDeliveryAttempts.Add(attempt);
        await SaveAsync(transaction, ct);

        var body = Encoding.UTF8.GetBytes(ev.Body);
        var id = delivery.Id.ToString();
        var stamp = timestamp.ToString(CultureInfo.InvariantCulture);
        return new PreparedSend(attempt.Id, new WebhookHttpRequest(new Uri(url!),
        [
            new("Pairrot-Webhook-Id", id),
            new("Pairrot-Webhook-Timestamp", stamp),
            new("Pairrot-Webhook-Signature", WebhookSignature.Header(signing, id, timestamp, body)),
        ], body));
    }

    private async Task CompleteAsync(
        Guid deliveryId, Guid token, Guid attemptId, DateTimeOffset started, WebhookHttpResponse response, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var now = jobs.Now();
        var attempt = await db.WebhookDeliveryAttempts.SingleAsync(a => a.Id == attemptId, ct);
        var outcome = response.Status is { } status
            ? (status is >= 300 and < 400 ? WebhookAttemptOutcome.PermanentFailure : WebhookRetryPolicy.Classify(status))
            : response.ErrorCode == WebhookHolds.DestinationNotAllowed ? WebhookAttemptOutcome.PermanentFailure : WebhookAttemptOutcome.RetryableFailure;
        var code = response.Status is { } s
            ? outcome == WebhookAttemptOutcome.Delivered ? null : s is >= 300 and < 400 ? "redirect_not_followed" : $"http_{s}"
            : response.ErrorCode;

        // Feature 014: a late result for an erased party records status and timing, never response text.
        var parties = await db.WebhookDeliveries.AsNoTracking().Where(d => d.Id == deliveryId)
            .Select(d => new { d.OwnerTenantId, d.SubscriberTenantId }).SingleAsync(ct);
        var erased = await DataBarrier.IsClosedAsync(db, parties.OwnerTenantId, ct)
            || await DataBarrier.IsClosedAsync(db, parties.SubscriberTenantId, ct);

        // The attempt is history and is completed even when the lease was lost meanwhile.
        if (attempt.Outcome == WebhookAttemptOutcome.InProgress && attempt.LeaseToken == token)
        {
            attempt.Outcome = outcome;
            attempt.HttpStatus = response.Status;
            attempt.ErrorCode = code;
            attempt.ResponseExcerpt = erased ? null : response.Excerpt ?? response.ErrorMessage;
            attempt.CompletedAt = now;
            attempt.DurationMs = (int)Math.Min(int.MaxValue, Math.Max(0, (now - started).TotalMilliseconds));
        }

        var delivery = await db.WebhookDeliveries.SingleAsync(d => d.Id == deliveryId, ct);
        if (delivery.State != WebhookDeliveryState.Running || delivery.LeaseToken != token)
        {
            logger.LogWarning("Webhook delivery {DeliveryId} lost its lease before its result was saved.", deliveryId);
            await SaveAsync(transaction, ct);
            return;
        }

        delivery.LastHttpStatus = response.Status;
        var message = erased ? null : response.ErrorMessage ?? (response.Status is { } httpStatus ? $"The receiver answered HTTP {httpStatus}." : null);
        if (outcome == WebhookAttemptOutcome.Delivered)
        {
            Finish(delivery, WebhookDeliveryState.Delivered, now, null, null, "delivered");
            delivery.DeliveredAt = now;
            var synthetic = await db.WebhookEvents.Where(e => e.Id == delivery.EventId).Select(e => e.Synthetic).SingleAsync(ct);
            await (synthetic
                ? db.WebhookEndpoints.Where(e => e.Id == delivery.EndpointId).ExecuteUpdateAsync(u => u.SetProperty(e => e.LastTestAcceptedAt, now), ct)
                : db.WebhookEndpoints.Where(e => e.Id == delivery.EndpointId).ExecuteUpdateAsync(u => u.SetProperty(e => e.LastSuccessAt, now), ct));
        }
        else if (code == WebhookHolds.DestinationNotAllowed)
        {
            Hold(delivery, WebhookHolds.DestinationNotAllowed, now);
            delivery.LastErrorCode = code;
            delivery.LastErrorMessage = message;
        }
        else if (outcome == WebhookAttemptOutcome.RetryableFailure
            && WebhookRetryPolicy.NextAttempt(delivery.AttemptInGeneration, now, WebhookRetryPolicy.RetryAfter(response.RetryAfter, now)) is { } next)
        {
            Finish(delivery, WebhookDeliveryState.RetryWait, now, code, message, "retryable_failure");
            delivery.DueAt = next;
        }
        else
        {
            Finish(delivery, WebhookDeliveryState.Failed, now, code,
                outcome == WebhookAttemptOutcome.RetryableFailure ? $"{message} Automatic attempts exhausted." : message,
                outcome == WebhookAttemptOutcome.RetryableFailure ? "attempts_exhausted" : "permanent_failure");
        }

        await SaveAsync(transaction, ct);
    }

    /// <summary>
    /// A worker stopped after claiming. With a reserved attempt, that attempt's outcome is unknown and
    /// counts as a retryable failure at the lease expiry; without one, the delivery returns to the queue.
    /// </summary>
    public async Task<int> RecoverExpiredAsync(CancellationToken ct = default)
    {
        var now = jobs.Now();
        var recovered = 0;
        while (true)
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var rows = await db.WebhookDeliveries
                .FromSql($"SELECT * FROM webhook_deliveries WHERE state = 'running' AND lease_expires_at <= {now} ORDER BY id LIMIT 1 FOR UPDATE SKIP LOCKED")
                .ToListAsync(ct);
            if (rows is not [var delivery])
            {
                return recovered;
            }

            var expiredAt = delivery.LeaseExpiresAt!.Value;
            var attempt = await db.WebhookDeliveryAttempts
                .SingleOrDefaultAsync(a => a.DeliveryId == delivery.Id && a.LeaseToken == delivery.LeaseToken && a.Outcome == WebhookAttemptOutcome.InProgress, ct);
            if (attempt is null)
            {
                Release(delivery, WebhookDeliveryState.Pending, now);
            }
            else
            {
                attempt.Outcome = WebhookAttemptOutcome.OutcomeUnknown;
                attempt.ErrorCode = "outcome_unknown";
                attempt.ResponseExcerpt = "The worker stopped before the result was recorded; the receiver may have accepted it.";
                attempt.CompletedAt = expiredAt;
                const string message = "The previous attempt's outcome is unknown; the receiver may have accepted it.";
                if (WebhookRetryPolicy.NextAttempt(delivery.AttemptInGeneration, expiredAt) is { } next)
                {
                    Finish(delivery, WebhookDeliveryState.RetryWait, now, "outcome_unknown", message, "outcome_unknown");
                    delivery.DueAt = next;
                }
                else
                {
                    Finish(delivery, WebhookDeliveryState.Failed, now, "outcome_unknown", $"{message} Automatic attempts exhausted.", "attempts_exhausted");
                }
            }

            await SaveAsync(transaction, ct);
            recovered++;
        }
    }

    /// <summary>
    /// Why the delivery must not be sent now: a changed destination, an environment mismatch, or
    /// missing current authority of the subscriber over the payment owner.
    /// </summary>
    public static async Task<string?> HoldReasonAsync(
        PairrotDbContext db, WebhookDelivery delivery, WebhookEndpoint endpoint, WebhookEvent ev, CancellationToken ct)
    {
        if (delivery.DestinationRevision != endpoint.DestinationRevision)
        {
            return WebhookHolds.DestinationChanged;
        }

        if (!ev.Synthetic && ev.Environment != endpoint.Environment)
        {
            return WebhookHolds.EnvironmentMismatch;
        }

        var tenants = await db.Tenants.AsNoTracking()
            .Where(t => t.Id == endpoint.TenantId || t.Id == delivery.OwnerTenantId)
            .Select(t => new { t.Id, t.Status, t.ParentTenantId, t.CanCreateSubtenants, ParentStatus = t.ParentTenant != null ? t.ParentTenant.Status : (TenantStatus?)null })
            .ToListAsync(ct);
        var subscriber = tenants.SingleOrDefault(t => t.Id == endpoint.TenantId);
        var owner = tenants.SingleOrDefault(t => t.Id == delivery.OwnerTenantId);
        if (subscriber is null || owner is null)
        {
            return WebhookHolds.NotAuthorized;
        }

        if (subscriber.Status != TenantStatus.Active || owner.Status != TenantStatus.Active
            || owner.ParentStatus is { } parentStatus && parentStatus != TenantStatus.Active
            || subscriber.ParentStatus is { } subscriberParent && subscriberParent != TenantStatus.Active)
        {
            return WebhookHolds.TenantNotActive;
        }

        var authorized = owner.Id == subscriber.Id
            || (endpoint.Scope == WebhookScope.OwnAndDirectChildren && owner.ParentTenantId == subscriber.Id && subscriber.CanCreateSubtenants);
        return authorized ? null : WebhookHolds.NotAuthorized;
    }

    /// <summary>The current key and a retiring key still in its grace period, decrypted at send time.</summary>
    private async Task<List<(string KeyId, byte[] Secret)>> SigningKeysAsync(Guid endpointId, DateTimeOffset now, CancellationToken ct)
    {
        var rows = await db.WebhookSigningKeys.AsNoTracking()
            .Where(k => k.EndpointId == endpointId && (k.State == WebhookKeyState.Current || (k.State == WebhookKeyState.Retiring && k.RetiresAt > now)))
            .OrderBy(k => k.State)
            .ToListAsync(ct);
        var result = new List<(string, byte[])>();
        foreach (var row in rows)
        {
            if (keys.Unprotect(row.ProtectedSecret!) is not { } secret)
            {
                return [];
            }

            result.Add((row.KeyId, secret));
        }

        return rows.Any(k => k.State == WebhookKeyState.Current) ? result : [];
    }

    internal static void Hold(WebhookDelivery delivery, string reason, DateTimeOffset now)
    {
        Release(delivery, WebhookDeliveryState.Held, now);
        delivery.HoldReason = reason;
        delivery.DueAt = null;
        delivery.LastOutcome = "held";
    }

    private static void Release(WebhookDelivery delivery, WebhookDeliveryState state, DateTimeOffset now)
    {
        delivery.State = state;
        delivery.LeaseToken = null;
        delivery.LeaseOwner = null;
        delivery.LeaseExpiresAt = null;
        delivery.UpdatedAt = now;
        delivery.Version++;
    }

    private static void Finish(
        WebhookDelivery delivery, WebhookDeliveryState state, DateTimeOffset now, string? code, string? message, string? outcome)
    {
        Release(delivery, state, now);
        delivery.HoldReason = null;
        delivery.DueAt = null;
        delivery.LastOutcome = outcome;
        delivery.LastErrorCode = code;
        delivery.LastErrorMessage = message is null ? null : message.Length <= 500 ? message : message[..500];
    }

    private async Task SaveAsync(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction, CancellationToken ct)
    {
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
}

public static class WebhookHolds
{
    public const string DestinationChanged = "destination_changed";
    public const string DestinationNotAllowed = "destination_not_allowed";
    public const string EnvironmentMismatch = "environment_mismatch";
    public const string TenantNotActive = "tenant_not_active";
    public const string NotAuthorized = "not_authorized";
    public const string SigningKeyUnavailable = "signing_key_unavailable";
}

/// <summary>Polls for due deliveries in the API process and runs maintenance periodically.</summary>
public sealed class WebhookDispatcher(
    IServiceScopeFactory scopes, IOptions<WebhookOptions> options, ILogger<WebhookDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextMaintenance = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                if (DateTimeOffset.UtcNow >= nextMaintenance)
                {
                    await scope.ServiceProvider.GetRequiredService<WebhookMaintenance>().RunAsync(stoppingToken);
                    nextMaintenance = DateTimeOffset.UtcNow + options.Value.MaintenanceInterval;
                }

                await scope.ServiceProvider.GetRequiredService<WebhookDeliveryWorker>().RunDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Webhook delivery round failed.");
            }

            try
            {
                await Task.Delay(options.Value.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
