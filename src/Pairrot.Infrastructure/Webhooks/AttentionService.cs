using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Pairrot.Core.Tenants;
using Pairrot.Infrastructure.Persistence;
using Pairrot.Infrastructure.Tenants;

namespace Pairrot.Infrastructure.Webhooks;

/// <summary>Feature 016's attention kinds. <see cref="OverdueRequest"/> is opt-in.</summary>
public static class AttentionKinds
{
    public const string UnmatchedTransaction = "unmatched_transaction";
    public const string ReviewCase = "review_case";
    public const string PausedAddress = "paused_address";
    public const string FailedDelivery = "failed_delivery";
    public const string OverpaidRequest = "overpaid_request";
    public const string OverdueRequest = "overdue_request";

    public static readonly IReadOnlyList<string> All =
        [UnmatchedTransaction, ReviewCase, PausedAddress, FailedDelivery, OverpaidRequest, OverdueRequest];

    /// <summary>Without <c>kinds</c>: everything but overdue requests, which an integrator with its own deadlines does not want.</summary>
    public static readonly IReadOnlyList<string> Default = All.Where(k => k != OverdueRequest).ToList();
}

/// <summary>One item waiting for a person. <see cref="Summary"/> is a JSON object without sender data or free text.</summary>
public sealed class AttentionItem
{
    public string Kind { get; set; } = "";
    public Guid TenantId { get; set; }
    public Guid? BankConnectionId { get; set; }
    public Guid SubjectId { get; set; }
    public DateTimeOffset Since { get; set; }
    public string Summary { get; set; } = "{}";
}

/// <summary>The position after an item, in list order <c>(since, kind, tenant_id, subject_id)</c>.</summary>
public sealed record AttentionCursor(DateTimeOffset Since, string Kind, Guid TenantId, Guid SubjectId)
{
    public static AttentionCursor After(AttentionItem item) => new(item.Since, item.Kind, item.TenantId, item.SubjectId);

    public string Encode() => Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"{Since.UtcTicks.ToString(CultureInfo.InvariantCulture)}|{Kind}|{TenantId}|{SubjectId}"))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static AttentionCursor? Decode(string text)
    {
        try
        {
            var padded = text.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(padded)).Split('|');
            return parts is [var ticks, var kind, var tenant, var subject]
                && long.TryParse(ticks, NumberStyles.None, CultureInfo.InvariantCulture, out var t) && AttentionKinds.All.Contains(kind)
                && Guid.TryParse(tenant, out var tenantId) && Guid.TryParse(subject, out var subjectId)
                    ? new AttentionCursor(new DateTimeOffset(t, TimeSpan.Zero), kind, tenantId, subjectId)
                    : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <param name="TenantId">One tenant in scope: the caller or, with management authority, a direct child.</param>
/// <param name="Since">Only items waiting since this time or later.</param>
public sealed record AttentionFilter(IReadOnlyList<string> Kinds, Guid? TenantId = null, DateTimeOffset? Since = null);

public sealed record AttentionPage(IReadOnlyList<AttentionItem> Items, AttentionCursor? Next);

public sealed record TenantAttentionCount(Guid TenantId, int Total, IReadOnlyDictionary<string, int> ByKind);

public sealed record AttentionSummary(int Total, IReadOnlyDictionary<string, int> ByKind, IReadOnlyList<TenantAttentionCount> ByTenant);

/// <summary>
/// Feature 016's attention list: what waits for a person, oldest first, computed from the domain
/// tables on every request (there is no item store and nothing to dismiss: an item disappears
/// when its cause is resolved). The caller sees its own items and, with management authority,
/// its direct children's business items; failed deliveries are those of the caller's own
/// endpoint. Deleted and erased tenants contribute nothing.
/// </summary>
public class AttentionService(PairrotDbContext db, TenantService tenants, TimeProvider time)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    public async Task<ServiceResult<AttentionPage>> ListAsync(
        Guid callerTenantId, bool managesChildren, AttentionFilter filter, int limit, AttentionCursor? after, CancellationToken ct = default)
    {
        if (await ScopeAsync(callerTenantId, managesChildren, filter, ct) is not ({ } owners, var endpointId))
        {
            return ServiceError.NotFound();
        }

        var (sql, parameters) = Build(owners, endpointId, filter);
        var where = new List<string>();
        if (after is not null)
        {
            where.Add("(since, kind, tenant_id, subject_id) > (@after_since, @after_kind, @after_tenant, @after_subject)");
            parameters.Add(new NpgsqlParameter("after_since", NpgsqlDbType.TimestampTz) { Value = after.Since.UtcDateTime });
            parameters.Add(new NpgsqlParameter("after_kind", NpgsqlDbType.Text) { Value = after.Kind });
            parameters.Add(new NpgsqlParameter("after_tenant", NpgsqlDbType.Uuid) { Value = after.TenantId });
            parameters.Add(new NpgsqlParameter("after_subject", NpgsqlDbType.Uuid) { Value = after.SubjectId });
        }

        parameters.Add(new NpgsqlParameter("limit", NpgsqlDbType.Integer) { Value = limit + 1 });
        var query = $"""
            {sql}
            SELECT kind, tenant_id, bank_connection_id, subject_id, since, summary FROM items
            {(where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "")}
            ORDER BY since, kind, tenant_id, subject_id
            LIMIT @limit
            """;
#pragma warning disable EF1002 // Built from constants; every value is a parameter.
        var items = await db.Database.SqlQueryRaw<AttentionItem>(query, parameters.ToArray()).ToListAsync(ct);
#pragma warning restore EF1002
        var hasMore = items.Count > limit;
        if (hasMore)
        {
            items.RemoveAt(items.Count - 1);
        }

        return new AttentionPage(items, hasMore ? AttentionCursor.After(items[^1]) : null);
    }

    public async Task<ServiceResult<AttentionSummary>> SummaryAsync(
        Guid callerTenantId, bool managesChildren, AttentionFilter filter, CancellationToken ct = default)
    {
        if (await ScopeAsync(callerTenantId, managesChildren, filter, ct) is not ({ } owners, var endpointId))
        {
            return ServiceError.NotFound();
        }

        var (sql, parameters) = Build(owners, endpointId, filter);
#pragma warning disable EF1002 // Built from constants; every value is a parameter.
        var counts = await db.Database.SqlQueryRaw<KindCount>(
            $"{sql}\nSELECT kind, tenant_id, count(*)::int AS count FROM items GROUP BY kind, tenant_id", parameters.ToArray()).ToListAsync(ct);
#pragma warning restore EF1002
        return new AttentionSummary(
            counts.Sum(c => c.Count),
            ByKind(counts),
            counts.GroupBy(c => c.TenantId).OrderBy(g => g.Key)
                .Select(g => new TenantAttentionCount(g.Key, g.Sum(c => c.Count), ByKind(g)))
                .ToList());
    }

    private static Dictionary<string, int> ByKind(IEnumerable<KindCount> counts) =>
        counts.GroupBy(c => c.Kind).OrderBy(g => AttentionKinds.All.ToList().IndexOf(g.Key)).ToDictionary(g => g.Key, g => g.Sum(c => c.Count));

    /// <summary>The business owners in scope and the caller's own endpoint, or <c>null</c> when the tenant filter is outside it.</summary>
    private async Task<(List<Guid> Owners, Guid? EndpointId)?> ScopeAsync(
        Guid callerTenantId, bool managesChildren, AttentionFilter filter, CancellationToken ct)
    {
        if (await tenants.FindAsync(callerTenantId, ct) is not { } caller)
        {
            return null;
        }

        var owners = new List<Guid>();
        if (caller.Status != TenantStatus.Deleted && caller.ErasureRequestedAt is null)
        {
            owners.Add(caller.Id);
        }

        if (managesChildren && caller.CanCreateSubtenants)
        {
            owners.AddRange(await db.Tenants.AsNoTracking()
                .Where(t => t.ParentTenantId == caller.Id && t.Status != TenantStatus.Deleted && t.ErasureRequestedAt == null)
                .Select(t => t.Id)
                .ToListAsync(ct));
        }

        if (filter.TenantId is { } only)
        {
            if (!owners.Contains(only))
            {
                return null;
            }

            owners = [only];
        }

        var endpointId = await db.WebhookEndpoints.AsNoTracking().Where(e => e.TenantId == caller.Id).Select(e => (Guid?)e.Id).SingleOrDefaultAsync(ct);
        return (owners, endpointId);
    }

    /// <summary>
    /// The <c>items</c> CTE. <c>since</c> is when the item started waiting: a transaction's
    /// recording or its latest revocation, a case's opening or latest reopening, the pause, the
    /// delivery's last state change, the first change of the current overpaid interval, the due time.
    /// </summary>
    private (string Sql, List<NpgsqlParameter> Parameters) Build(List<Guid> owners, Guid? endpointId, AttentionFilter filter)
    {
        var parameters = new List<NpgsqlParameter>
        {
            new("owners", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = owners.ToArray() },
            new("kinds", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = filter.Kinds.ToArray() },
            new("now", NpgsqlDbType.TimestampTz) { Value = time.GetUtcNow().UtcDateTime },
            new("endpoint", NpgsqlDbType.Uuid) { Value = (object?)endpointId ?? DBNull.Value },
        };
        var since = "";
        if (filter.Since is { } from)
        {
            since = "WHERE since >= @since";
            parameters.Add(new NpgsqlParameter("since", NpgsqlDbType.TimestampTz) { Value = from.UtcDateTime });
        }

        var sql = $"""
            WITH all_items AS (
                SELECT 'unmatched_transaction' AS kind, t.tenant_id, t.bank_connection_id, t.id AS subject_id,
                    GREATEST(t.created_at, (SELECT max(a.revoked_at) FROM payment_allocations a WHERE a.transaction_id = t.id)) AS since,
                    jsonb_build_object('amount', t.amount, 'currency', t.currency, 'variable_symbol', t.variable_symbol,
                        'result', t.match_result, 'payment_request_id', t.match_request_id, 'external_id', r.external_id,
                        'expected_amount', r.amount)::text AS summary
                FROM incoming_transactions t
                LEFT JOIN payment_requests r ON r.id = t.match_request_id
                WHERE 'unmatched_transaction' = ANY(@kinds) AND t.tenant_id = ANY(@owners)
                    AND (t.ingestion_status = 'accepted' OR t.manually_confirmed) AND NOT t.movement_conflict
                    AND t.source_kind = 'received' AND t.environment = 'live'
                    AND t.handling = 'unallocated' AND t.match_result IS DISTINCT FROM 'matched'
                    AND NOT EXISTS (SELECT 1 FROM payment_review_case_members m JOIN payment_review_cases c ON c.id = m.case_id
                        WHERE m.transaction_id = t.id AND c.state = 'open')
                UNION ALL
                SELECT 'review_case', c.tenant_id, c.bank_connection_id, c.id,
                    GREATEST(c.created_at, (SELECT max(d.created_at) FROM payment_review_decisions d WHERE d.case_id = c.id AND d.action = 'reopen')),
                    jsonb_build_object('issue', c.issue, 'reason_code', c.reason_code,
                        'transaction_count', (SELECT count(DISTINCT m.transaction_id) FROM payment_review_case_members m WHERE m.case_id = c.id))::text
                FROM payment_review_cases c
                WHERE 'review_case' = ANY(@kinds) AND c.tenant_id = ANY(@owners) AND c.state = 'open'
                UNION ALL
                SELECT 'paused_address', a.tenant_id, a.bank_connection_id, a.id, a.paused_at,
                    jsonb_build_object('inbound_address', a.address, 'operator_only_resume', a.operator_resume_required)::text
                FROM inbound_addresses a
                WHERE 'paused_address' = ANY(@kinds) AND a.tenant_id = ANY(@owners)
                    AND a.processing = 'paused_foreign_mail' AND a.state = 'active'
                UNION ALL
                SELECT 'failed_delivery', d.owner_tenant_id, NULL::uuid, d.id, d.updated_at,
                    jsonb_build_object('endpoint_id', d.endpoint_id, 'event_id', d.event_id, 'event_type', e.type, 'state', d.state,
                        'hold_reason', d.hold_reason, 'last_error_code', d.last_error_code, 'last_http_status', d.last_http_status)::text
                FROM webhook_deliveries d
                JOIN webhook_events e ON e.id = d.event_id
                WHERE 'failed_delivery' = ANY(@kinds) AND d.endpoint_id = @endpoint AND d.owner_tenant_id = ANY(@owners)
                    AND d.state IN ('failed', 'held')
                UNION ALL
                SELECT 'overpaid_request', r.tenant_id, r.bank_connection_id, r.id,
                    COALESCE((SELECT max(ch.created_at) FROM payment_request_changes ch
                        WHERE ch.payment_request_id = r.id AND ch.overpaid_before = 0 AND ch.overpaid_after > 0), r.updated_at),
                    jsonb_build_object('external_id', r.external_id, 'variable_symbol', r.variable_symbol, 'amount', r.amount,
                        'paid_amount', r.paid_amount, 'excess_amount', r.paid_amount - r.amount, 'currency', r.currency)::text
                FROM payment_requests r
                WHERE 'overpaid_request' = ANY(@kinds) AND r.tenant_id = ANY(@owners) AND r.paid_amount > r.amount
                UNION ALL
                SELECT 'overdue_request', r.tenant_id, r.bank_connection_id, r.id, r.due_at,
                    jsonb_build_object('external_id', r.external_id, 'variable_symbol', r.variable_symbol, 'amount', r.amount,
                        'paid_amount', r.paid_amount, 'remaining_amount', r.amount - r.paid_amount, 'currency', r.currency,
                        'due_at', r.due_at)::text
                FROM payment_requests r
                WHERE 'overdue_request' = ANY(@kinds) AND r.tenant_id = ANY(@owners)
                    AND r.status IN ('open', 'partially_paid') AND r.due_at < @now
            ),
            items AS (SELECT * FROM all_items {since})
            """;
        return (sql, parameters);
    }

    private sealed class KindCount
    {
        public string Kind { get; set; } = "";
        public Guid TenantId { get; set; }
        public int Count { get; set; }
    }
}
