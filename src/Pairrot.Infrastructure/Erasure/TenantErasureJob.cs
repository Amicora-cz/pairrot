using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pairrot.Core.Inbound;
using Pairrot.Core.Jobs;
using Pairrot.Core.Tenants;
using Pairrot.Infrastructure.Jobs;
using Pairrot.Infrastructure.Persistence;
using Pairrot.Infrastructure.Tenants;

namespace Pairrot.Infrastructure.Erasure;

/// <summary>
/// Feature 014: erases a tenant whose erasure was requested, in batches of
/// <c>Erasure:BatchSize</c>. Each batch is its own transaction, committed only while this worker
/// holds the job's lease and together with its counts, so an interrupted job is resumed by another
/// worker and the final counts equal those of an uninterrupted run. Each step changes only rows
/// that still hold personal data; a pass that changes nothing is the final verification. Webhook
/// sends already in flight are waited for, since their bodies and results are purged after them.
/// </summary>
public class TenantErasureJob(
    PairrotDbContext db, BackgroundJobQueue jobs, TenantService tenants, IOptions<ErasureOptions> options,
    ILogger<TenantErasureJob> logger) : IBackgroundJobHandler
{
    private const int MaxPasses = 5;
    private const string OwnEndpoints = "(SELECT e.id FROM webhook_endpoints e WHERE e.tenant_id = {0})";
    private const string OwnDeliveries = $"(x.owner_tenant_id = {{0}} OR x.endpoint_id IN {OwnEndpoints})";
    private const string UnsentStates = "('pending', 'retry_wait', 'held', 'failed')";
    private const string Placeholder = $"'{ErasureRules.Placeholder}'";
    private const string MaskedAccountNumber =
        "'…' || right(regexp_replace(split_part(account_number, '/', 1), '[^0-9]', '', 'g'), 2) || '/' || split_part(account_number, '/', 2)";
    private const string MaskedIban = "'CZ…' || substr(iban, 5, 4) || '…' || right(iban, 2)";

    /// <summary>
    /// One record type's purge: SQL changing at most <c>{1}</c> rows of tenant <c>{0}</c> that still
    /// hold the data, at time <c>{2}</c>. The name is its key in the erasure's counts.
    /// </summary>
    private sealed record Step(string Name, string Sql);

    private static readonly Step[] Steps =
    [
        new("tenants", $$"""
            UPDATE tenants SET name = {{Placeholder}}, contact_email = NULL, external_id = '{{ErasureRules.TombstonePrefix}}' || id::text, updated_at = {2}
            WHERE id = {0} AND (name <> {{Placeholder}} OR contact_email IS NOT NULL OR external_id <> '{{ErasureRules.TombstonePrefix}}' || id::text)
            """),
        new("tenant_memberships", "DELETE FROM tenant_memberships WHERE tenant_id = {0}"),
        new("tenant_api_keys", "UPDATE tenant_api_keys SET label = NULL WHERE tenant_id = {0} AND label IS NOT NULL"),
        new("bank_connections", $$"""
            UPDATE bank_connections SET label = NULL, account_number = {{MaskedAccountNumber}}, iban = {{MaskedIban}},
                status = 'archived', archived_at = coalesce(archived_at, {2}), archived_reason = coalesce(archived_reason, 'tenant_deleted'),
                erased_at = {2}, updated_at = {2}
            WHERE id IN (SELECT id FROM bank_connections WHERE tenant_id = {0} AND erased_at IS NULL ORDER BY id LIMIT {1})
            """),
        new("inbound_addresses", "UPDATE inbound_addresses SET provisioning_error = NULL WHERE tenant_id = {0} AND provisioning_error IS NOT NULL"),
        new("forwarding_sources", """
            UPDATE forwarding_sources SET address = NULL, erased_at = {2}, updated_at = {2}
            WHERE id IN (SELECT id FROM forwarding_sources WHERE tenant_id = {0} AND erased_at IS NULL ORDER BY id LIMIT {1})
            """),
        new("payment_requests", $$"""
            UPDATE payment_requests SET external_id = '{{ErasureRules.TombstonePrefix}}' || id::text, message = NULL, description = NULL,
                cancellation_reason = NULL, account_number = {{MaskedAccountNumber}}, iban = {{MaskedIban}}, qr_payload = NULL, erased_at = {2}
            WHERE id IN (SELECT id FROM payment_requests WHERE tenant_id = {0} AND erased_at IS NULL ORDER BY id LIMIT {1})
            """),
        new("payment_request_changes", $$"""
            UPDATE payment_request_changes SET external_id = '{{ErasureRules.TombstonePrefix}}' || payment_request_id::text
            WHERE id IN (SELECT id FROM payment_request_changes
                WHERE tenant_id = {0} AND external_id NOT LIKE '{{ErasureRules.TombstonePrefix}}%' ORDER BY id LIMIT {1})
            """),
        new("webhook_events", """
            UPDATE webhook_events SET body = NULL, payload_erased_at = {2}
            WHERE id IN (SELECT id FROM webhook_events WHERE tenant_id = {0} AND body IS NOT NULL ORDER BY id LIMIT {1})
            """),
        new("webhook_deliveries", $$"""
            UPDATE webhook_deliveries d SET
                state = CASE WHEN d.state IN {{UnsentStates}} THEN 'abandoned' ELSE d.state END,
                hold_reason = CASE WHEN d.state IN {{UnsentStates}} THEN NULL ELSE d.hold_reason END,
                due_at = CASE WHEN d.state IN {{UnsentStates}} THEN NULL ELSE d.due_at END,
                last_outcome = CASE WHEN d.state IN {{UnsentStates}} THEN 'abandoned' ELSE d.last_outcome END,
                abandon_reason = CASE WHEN d.state IN {{UnsentStates}} OR d.abandon_reason IS NOT NULL THEN '{{ErasureRules.AbandonReason}}' END,
                last_error_message = NULL, updated_at = {2}, version = d.version + 1
            WHERE d.id IN (SELECT x.id FROM webhook_deliveries x WHERE {{OwnDeliveries}} AND x.state <> 'running'
                AND (x.state IN {{UnsentStates}} OR x.last_error_message IS NOT NULL
                    OR (x.abandon_reason IS NOT NULL AND x.abandon_reason <> '{{ErasureRules.AbandonReason}}'))
                ORDER BY x.id LIMIT {1})
            """),
        new("webhook_delivery_attempts", $$"""
            UPDATE webhook_delivery_attempts a SET response_excerpt = NULL,
                url = CASE WHEN x.endpoint_id IN {{OwnEndpoints}} THEN NULL ELSE a.url END
            FROM webhook_deliveries x
            WHERE x.id = a.delivery_id AND a.id IN (SELECT a2.id FROM webhook_delivery_attempts a2 JOIN webhook_deliveries x ON x.id = a2.delivery_id
                WHERE {{OwnDeliveries}} AND a2.outcome <> 'in_progress'
                  AND (a2.response_excerpt IS NOT NULL OR (a2.url IS NOT NULL AND x.endpoint_id IN {{OwnEndpoints}}))
                ORDER BY a2.id LIMIT {1})
            """),
        new("webhook_endpoints", """
            UPDATE webhook_endpoints SET url = NULL, state = 'paused', erased_at = {2}, updated_at = {2}, version = version + 1
            WHERE tenant_id = {0} AND erased_at IS NULL
            """),
        new("webhook_destination_revisions", $"UPDATE webhook_destination_revisions SET url = NULL WHERE url IS NOT NULL AND endpoint_id IN {OwnEndpoints}"),
        new("webhook_signing_keys", $$"""
            UPDATE webhook_signing_keys SET state = 'retired', protected_secret = NULL,
                retires_at = coalesce(retires_at, {2}), retired_at = coalesce(retired_at, {2})
            WHERE endpoint_id IN {{OwnEndpoints}} AND (state <> 'retired' OR protected_secret IS NOT NULL)
            """),
        new("inbound_messages", ""), // PersonalDataPurge.MessagesAsync, see RunStepAsync.
        new("inbound_delivery_receipts", """
            UPDATE inbound_delivery_receipts SET raw_payload = NULL
            WHERE id IN (SELECT r.id FROM inbound_delivery_receipts r JOIN inbound_messages m ON m.id = r.inbound_message_id
                WHERE m.tenant_id = {0} AND r.raw_payload IS NOT NULL ORDER BY r.id LIMIT {1})
            """),
        new("message_parse_runs", ""), // PersonalDataPurge.ParseRunsOfTenantAsync.
        new("incoming_transactions", $$"""
            UPDATE incoming_transactions SET {{PersonalDataPurge.PayerFieldsSql}},
                handling_reason = CASE WHEN handling_reason IS NULL THEN NULL ELSE {{Placeholder}} END,
                payer_data_purged_at = coalesce(payer_data_purged_at, {2}), erased_at = {2}
            WHERE id IN (SELECT id FROM incoming_transactions WHERE tenant_id = {0} AND erased_at IS NULL ORDER BY id LIMIT {1})
            """),
        new("payment_allocations", $$"""
            UPDATE payment_allocations SET
                reason = CASE WHEN reason IS NULL THEN NULL ELSE {{Placeholder}} END,
                revocation_reason = CASE WHEN revocation_reason IS NULL THEN NULL ELSE {{Placeholder}} END
            WHERE id IN (SELECT id FROM payment_allocations WHERE tenant_id = {0}
                AND (reason <> {{Placeholder}} OR revocation_reason <> {{Placeholder}}) ORDER BY id LIMIT {1})
            """),
        new("payment_review_decisions", $$"""
            UPDATE payment_review_decisions SET reason = {{Placeholder}}
            WHERE id IN (SELECT id FROM payment_review_decisions WHERE tenant_id = {0} AND reason <> {{Placeholder}} ORDER BY id LIMIT {1})
            """),
        new("payment_review_cases", $$"""
            UPDATE payment_review_cases SET reason_message = {{Placeholder}}
            WHERE id IN (SELECT id FROM payment_review_cases WHERE tenant_id = {0} AND reason_message <> {{Placeholder}} ORDER BY id LIMIT {1})
            """),
        new("manual_command_receipts", """
            UPDATE manual_command_receipts SET response_body = NULL,
                idempotency_key = 'sha256:' || encode(sha256(convert_to(idempotency_key, 'UTF8')), 'hex')
            WHERE id IN (SELECT id FROM manual_command_receipts WHERE tenant_id = {0} AND response_body IS NOT NULL ORDER BY id LIMIT {1})
            """),
        new("background_jobs", """
            UPDATE background_jobs SET last_error_message = NULL
            WHERE id IN (SELECT j.id FROM background_jobs j WHERE j.last_error_message IS NOT NULL
                AND (j.target_id IN (SELECT id FROM inbound_messages WHERE tenant_id = {0})
                    OR j.target_id IN (SELECT id FROM incoming_transactions WHERE tenant_id = {0})
                    OR j.target_id IN (SELECT id FROM payment_requests WHERE tenant_id = {0}))
                ORDER BY j.id LIMIT {1})
            """),
        new("tenant_audit_events", """
            UPDATE tenant_audit_events SET details = jsonb_build_object('erased', true)
            WHERE id IN (SELECT id FROM tenant_audit_events
                WHERE target_tenant_id = {0} AND details <> jsonb_build_object('erased', true) ORDER BY id LIMIT {1})
            """),
    ];

    public string Type => BackgroundJobTypes.EraseTenant;

    /// <summary>Erasure needs only the database, so every worker can run it.</summary>
    public bool IsEnabled => true;

    public async Task HandleAsync(JobLease lease, CancellationToken ct)
    {
        var tenantId = lease.TargetId;
        var erasure = await db.TenantErasures.AsNoTracking().SingleOrDefaultAsync(e => e.TenantId == tenantId, ct);
        if (erasure is null || erasure.State == TenantErasureState.Done)
        {
            await jobs.FinishAsync(lease, job =>
            {
                if (erasure is null)
                {
                    jobs.Fail(job, "erasure_not_found", "The tenant has no requested erasure.");
                }
                else
                {
                    jobs.Succeed(job);
                }

                return Task.CompletedTask;
            }, ct);
            return;
        }

        var now = jobs.Now();
        await db.TenantErasures.Where(e => e.Id == erasure.Id && e.State == TenantErasureState.Queued)
            .ExecuteUpdateAsync(u => u.SetProperty(e => e.State, TenantErasureState.Running).SetProperty(e => e.StartedAt, now), ct);

        for (var pass = 1; ; pass++)
        {
            var changed = 0;
            foreach (var step in Steps)
            {
                while (true)
                {
                    if (await RunStepAsync(lease, erasure.Id, tenantId, step, ct) is not { } count)
                    {
                        logger.LogWarning("Erasure job {JobId} lost its lease; another worker resumes it.", lease.JobId);
                        return;
                    }

                    changed += count;
                    if (count < options.Value.BatchSize)
                    {
                        break;
                    }
                }
            }

            if (changed == 0)
            {
                break;
            }

            if (pass == MaxPasses)
            {
                // Something keeps writing personal data; never report done while it does.
                await FinishAsync(lease, job => BackgroundJobQueue.Retry(job, jobs.Now() + TimeSpan.FromMinutes(1),
                    "erasure_not_converged", "Personal data was still being written; the erasure continues."), ct);
                return;
            }
        }

        // A request already sent cannot be recalled, but its result must not be written after done.
        var inFlight = await db.WebhookDeliveries.AsNoTracking()
            .Where(d => d.State == Core.Webhooks.WebhookDeliveryState.Running
                && (d.OwnerTenantId == tenantId || db.WebhookEndpoints.Any(e => e.Id == d.EndpointId && e.TenantId == tenantId)))
            .MaxAsync(d => d.LeaseExpiresAt, ct);
        if (inFlight is { } until)
        {
            await FinishAsync(lease, job => BackgroundJobQueue.Retry(job, until + TimeSpan.FromSeconds(1),
                "webhook_send_in_flight", "Waiting for webhook requests already sent to finish or time out."), ct);
            return;
        }

        var sent = await db.WebhookDeliveryAttempts.AsNoTracking()
            .AnyAsync(a => db.WebhookDeliveries.Any(d => d.Id == a.DeliveryId
                && (d.OwnerTenantId == tenantId || db.WebhookEndpoints.Any(e => e.Id == d.EndpointId && e.TenantId == tenantId))), ct);
        await FinishAsync(lease, async job =>
        {
            var tracked = await db.TenantErasures.SingleAsync(e => e.Id == erasure.Id, ct);
            var tenant = await db.Tenants.SingleAsync(t => t.Id == tenantId, ct);
            var completed = jobs.Now();
            tracked.State = TenantErasureState.Done;
            tracked.CompletedAt = completed;
            tracked.Reason = null;
            tracked.Limitations = JsonSerializer.Serialize(sent ? new[] { "sent_webhook_copies_not_recalled" } : []);
            tenant.ErasedAt = completed;
            tenant.UpdatedAt = completed;

            // The only record of what was removed: counts per record type, nothing about the content.
            tenants.AddAudit(Actor.Administrator("pairrot:erasure"), tenantId, AuditActions.TenantErased, new
            {
                ErasureId = tracked.Id,
                Counts = JsonSerializer.Deserialize<JsonElement>(tracked.Counts),
            });
            jobs.Succeed(job);
        }, ct);
    }

    /// <summary>
    /// Runs one batch of <paramref name="step"/> and adds its count, in one transaction that also
    /// locks the job and checks the lease. <c>null</c> when the lease was lost: nothing is saved.
    /// </summary>
    private async Task<int?> RunStepAsync(JobLease lease, Guid erasureId, Guid tenantId, Step step, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var held = await db.Database.SqlQuery<int>($$"""
            SELECT 1 AS "Value" FROM background_jobs WHERE id = {{lease.JobId}} AND state = 'running' AND lease_token = {{lease.Token}} FOR UPDATE
            """).ToListAsync(ct);
        if (held.Count == 0)
        {
            return null;
        }

        var batch = Math.Max(1, options.Value.BatchSize);
        var now = jobs.Now();
        var count = step.Name switch
        {
            "inbound_messages" => await PersonalDataPurge.MessagesAsync(db,
                (await db.InboundMessages.AsNoTracking()
                    .Where(m => m.TenantId == tenantId && m.ContentState != InboundContentState.Erased)
                    .OrderBy(m => m.Id).Select(m => m.Id).Take(batch).ToListAsync(ct)).ToArray(),
                InboundContentState.Erased, now, ct),
            "message_parse_runs" => await PersonalDataPurge.ParseRunsOfTenantAsync(db, tenantId, batch, now, ct),
            _ => await db.Database.ExecuteSqlRawAsync(step.Sql, [tenantId, batch, now], ct),
        };

        if (count > 0)
        {
            await db.Database.ExecuteSqlAsync($$"""
                UPDATE tenant_erasures SET counts = jsonb_set(counts, ARRAY[{{step.Name}}], to_jsonb(coalesce((counts ->> {{step.Name}})::int, 0) + {{count}}))
                WHERE id = {{erasureId}}
                """, ct);
        }

        await RenewAsync(lease, ct);
        await transaction.CommitAsync(ct);
        return count;
    }

    private async Task RenewAsync(JobLease lease, CancellationToken ct) => await jobs.RenewAsync(lease, ct);

    private async Task FinishAsync(JobLease lease, Func<BackgroundJob, Task> apply, CancellationToken ct)
    {
        if (!await jobs.FinishAsync(lease, apply, ct))
        {
            logger.LogWarning("Erasure job {JobId} lost its lease before finishing; its result was discarded.", lease.JobId);
        }
    }

    private Task FinishAsync(JobLease lease, Action<BackgroundJob> apply, CancellationToken ct) =>
        FinishAsync(lease, job =>
        {
            apply(job);
            return Task.CompletedTask;
        }, ct);
}
