using Microsoft.EntityFrameworkCore;
using Pairrot.Core.Inbound;
using Pairrot.Core.Tenants;
using Pairrot.Infrastructure.Persistence;

// Raw SQL here is composed from constant fragments only; every value is a parameter.
#pragma warning disable EF1002, EF1003

namespace Pairrot.Infrastructure.Erasure;

/// <summary>
/// The purges shared by payer-data retention and tenant erasure (Feature 014). Each statement
/// changes only rows that still hold the data, so repeating it changes nothing and its row count
/// is what it purged now.
/// </summary>
internal static class PersonalDataPurge
{
    /// <summary>Text left in required free-text fields after retention.</summary>
    public const string ExpiredText = "expired";

    /// <summary>
    /// Every sender and content field of the messages; only the reserved Pairrot routing address
    /// stays among the recipients. Codes, digests, and opaque provider IDs remain.
    /// </summary>
    public static Task<int> MessagesAsync(PairrotDbContext db, Guid[] ids, InboundContentState state, DateTimeOffset now, CancellationToken ct)
    {
        var target = state == InboundContentState.Erased ? "erased" : "expired";
        return db.Database.ExecuteSqlAsync($"""
            UPDATE inbound_messages m SET
                content_state = {target},
                subject = NULL, header_from = NULL, envelope_sender = NULL, sender_domain = NULL,
                envelope_recipients = ARRAY(
                    SELECT a.address FROM inbound_addresses a WHERE a.id = m.inbound_address_id AND a.address = ANY(m.envelope_recipients)),
                content_recipients = NULL, text_body = NULL, html_body = NULL, headers = NULL, attachments = NULL,
                raw_provider_content = NULL, transport_metadata = NULL, authentication_raw = NULL, fetch_error_message = NULL,
                updated_at = {now}
            WHERE m.id = ANY({ids}) AND m.content_state <> 'erased' AND m.content_state <> {target}
            """, ct);
    }

    /// <summary>Raw transport payloads of every delivery of the messages, duplicates included.</summary>
    public static Task<int> ReceiptsAsync(PairrotDbContext db, Guid[] messageIds, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync(
            $"UPDATE inbound_delivery_receipts SET raw_payload = NULL WHERE inbound_message_id = ANY({messageIds}) AND raw_payload IS NOT NULL", ct);

    /// <summary>Candidates, trust evidence, and reason texts of the messages' parse runs; codes stay.</summary>
    public static Task<int> ParseRunsOfMessagesAsync(PairrotDbContext db, Guid[] messageIds, DateTimeOffset now, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            $"UPDATE message_parse_runs SET {RunFieldsSql}, purged_at = {{0}} WHERE inbound_message_id = ANY({{1}}) AND purged_at IS NULL",
            [now, messageIds], ct);

    /// <summary>The same for every run of a tenant.</summary>
    public static Task<int> ParseRunsOfTenantAsync(PairrotDbContext db, Guid tenantId, int limit, DateTimeOffset now, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            $"UPDATE message_parse_runs SET {RunFieldsSql}, purged_at = {{0}} WHERE id IN "
            + "(SELECT id FROM message_parse_runs WHERE tenant_id = {1} AND purged_at IS NULL ORDER BY id LIMIT {2})",
            [now, tenantId, limit], ct);

    private static string RunFieldsSql =>
        $"candidate = NULL, trust_evidence = NULL, reason_message = '', reasons = {StripMessages("reasons", "code")}";

    /// <summary>Errors of jobs that worked on the records; they may quote provider or parser text.</summary>
    public static Task<int> JobErrorsAsync(PairrotDbContext db, Guid[] targetIds, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync(
            $"UPDATE background_jobs SET last_error_message = NULL WHERE target_id = ANY({targetIds}) AND last_error_message IS NOT NULL", ct);

    /// <summary>
    /// Review cases about the messages: their explanation may quote the parser's reason. Codes,
    /// evidence keys (critical facts), and members stay.
    /// </summary>
    public static Task<int> ReviewCasesOfMessagesAsync(PairrotDbContext db, Guid[] messageIds, string text, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"""
            UPDATE payment_review_cases c SET reason_message = {text}
            WHERE c.reason_message <> {text} AND c.reason_message <> {ErasureRules.Placeholder}
              AND (c.inbound_message_id = ANY({messageIds}) OR EXISTS (
                SELECT 1 FROM payment_review_case_members cm JOIN message_parse_runs r ON r.id = cm.parse_run_id
                WHERE cm.case_id = c.id AND r.inbound_message_id = ANY({messageIds})))
            """, ct);

    /// <summary>
    /// SQL assignments purging the sender fields and ingestion reason texts of transactions, for
    /// raw statements. Amounts, symbols, references, dates, statuses, and allocations stay.
    /// </summary>
    public static string PayerFieldsSql =>
        $"sender_account = NULL, sender_account_raw = NULL, sender_name = NULL, message = NULL, ingestion_reasons = {StripMessages("ingestion_reasons", null)}";

    /// <summary>
    /// A JSON array (stored as text) with every element's <c>message</c> emptied, in order. With
    /// <paramref name="keep"/>, elements keep only that key besides the empty message.
    /// </summary>
    private static string StripMessages(string column, string? keep) => keep is null
        ? $"coalesce((SELECT jsonb_agg(e.r || jsonb_build_object('message', '') ORDER BY e.n)::text FROM jsonb_array_elements({column}::jsonb) WITH ORDINALITY e(r, n)), '[]')"
        : $"coalesce((SELECT jsonb_agg(jsonb_build_object('{keep}', e.r -> '{keep}', 'message', '') ORDER BY e.n)::text FROM jsonb_array_elements({column}::jsonb) WITH ORDINALITY e(r, n)), '[]')";
}
