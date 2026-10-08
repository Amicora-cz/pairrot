using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pairrot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SubtenantErasure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "payment_requests_iban_format",
                table: "payment_requests");

            migrationBuilder.DropCheckConstraint(
                name: "inbound_messages_content_consistent",
                table: "inbound_messages");

            migrationBuilder.DropCheckConstraint(
                name: "inbound_messages_content_state_valid",
                table: "inbound_messages");

            migrationBuilder.DropCheckConstraint(
                name: "inbound_messages_foreign_minimal",
                table: "inbound_messages");

            migrationBuilder.DropCheckConstraint(
                name: "forwarding_sources_address_normalized",
                table: "forwarding_sources");

            migrationBuilder.DropCheckConstraint(
                name: "bank_connections_iban_format",
                table: "bank_connections");

            migrationBuilder.AlterColumn<string>(
                name: "body",
                table: "webhook_events",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "payload_erased_at",
                table: "webhook_events",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "url",
                table: "webhook_endpoints",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(2000)",
                oldMaxLength: 2000);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "erased_at",
                table: "webhook_endpoints",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "url",
                table: "webhook_destination_revisions",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(2000)",
                oldMaxLength: 2000);

            migrationBuilder.AlterColumn<string>(
                name: "url",
                table: "webhook_delivery_attempts",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(2000)",
                oldMaxLength: 2000);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "erased_at",
                table: "tenants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "erasure_requested_at",
                table: "tenants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "qr_payload",
                table: "payment_requests",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(1000)",
                oldMaxLength: 1000);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "erased_at",
                table: "payment_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "purged_at",
                table: "message_parse_runs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "response_body",
                table: "manual_command_receipts",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "erased_at",
                table: "incoming_transactions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "payer_data_purged_at",
                table: "incoming_transactions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "address",
                table: "forwarding_sources",
                type: "character varying(254)",
                maxLength: 254,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(254)",
                oldMaxLength: 254);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "erased_at",
                table: "forwarding_sources",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "erased_at",
                table: "bank_connections",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "tenant_erasures",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    requested_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    idempotency_key_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    counts = table.Column<string>(type: "jsonb", nullable: false),
                    limitations = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_erasures", x => x.id);
                    table.CheckConstraint("tenant_erasures_completion_consistent", "(state = 'done' AND completed_at IS NOT NULL AND reason IS NULL) OR (state <> 'done' AND completed_at IS NULL)");
                    table.CheckConstraint("tenant_erasures_counts_object", "jsonb_typeof(counts) = 'object'");
                    table.CheckConstraint("tenant_erasures_key_digest_format", "idempotency_key_sha256 IS NULL OR idempotency_key_sha256 ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("tenant_erasures_limitations_array", "jsonb_typeof(limitations) = 'array'");
                    table.CheckConstraint("tenant_erasures_state_valid", "state IN ('queued', 'running', 'done')");
                    table.ForeignKey(
                        name: "fk_tenant_erasures_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "webhook_events_payload_consistent",
                table: "webhook_events",
                sql: "(body IS NULL) = (payload_erased_at IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "webhook_endpoints_erasure_consistent",
                table: "webhook_endpoints",
                sql: "(erased_at IS NULL AND url IS NOT NULL) OR (erased_at IS NOT NULL AND url IS NULL AND state = 'paused')");

            migrationBuilder.AddCheckConstraint(
                name: "tenants_erasure_consistent",
                table: "tenants",
                sql: "(erasure_requested_at IS NULL AND erased_at IS NULL) OR (erasure_requested_at IS NOT NULL AND status = 'deleted' AND (erased_at IS NULL OR erased_at >= erasure_requested_at))");

            migrationBuilder.AddCheckConstraint(
                name: "payment_requests_erasure_consistent",
                table: "payment_requests",
                sql: "(erased_at IS NULL AND qr_payload IS NOT NULL) OR (erased_at IS NOT NULL AND qr_payload IS NULL AND message IS NULL AND description IS NULL AND cancellation_reason IS NULL AND external_id LIKE 'erased:%')");

            migrationBuilder.AddCheckConstraint(
                name: "payment_requests_iban_format",
                table: "payment_requests",
                sql: "iban ~ '^CZ[0-9]{22}$' OR (erased_at IS NOT NULL AND iban ~ '^CZ…[0-9]{4}…[0-9]{2}$')");

            migrationBuilder.AddCheckConstraint(
                name: "inbound_messages_content_consistent",
                table: "inbound_messages",
                sql: "(content_state = 'available' AND content_sha256 IS NOT NULL AND content_stored_at IS NOT NULL) OR (content_state = 'pending' AND content_sha256 IS NULL AND content_stored_at IS NULL) OR (content_state = 'failed' AND content_sha256 IS NULL AND content_stored_at IS NULL AND fetch_error_code IS NOT NULL) OR (content_state = 'skipped' AND content_sha256 IS NULL AND content_stored_at IS NULL AND text_body IS NULL AND html_body IS NULL AND headers IS NULL AND attachments IS NULL AND raw_provider_content IS NULL) OR (content_state IN ('expired', 'erased') AND text_body IS NULL AND html_body IS NULL AND headers IS NULL AND attachments IS NULL AND raw_provider_content IS NULL AND content_recipients IS NULL AND subject IS NULL AND header_from IS NULL AND envelope_sender IS NULL AND sender_domain IS NULL AND transport_metadata IS NULL AND authentication_raw IS NULL AND fetch_error_message IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "inbound_messages_content_state_valid",
                table: "inbound_messages",
                sql: "content_state IN ('pending', 'available', 'failed', 'skipped', 'expired', 'erased')");

            migrationBuilder.AddCheckConstraint(
                name: "inbound_messages_foreign_minimal",
                table: "inbound_messages",
                sql: "(sender_class IS DISTINCT FROM 'foreign' AND hold_reason IS DISTINCT FROM 'address_paused') OR (content_state IN ('skipped', 'expired', 'erased') AND subject IS NULL AND header_from IS NULL AND envelope_sender IS NULL AND transport_metadata IS NULL AND retain_until IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "forwarding_sources_address_normalized",
                table: "forwarding_sources",
                sql: "(address = lower(btrim(address)) AND address LIKE '_%@_%') OR (address IS NULL AND erased_at IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "bank_connections_erased_archived",
                table: "bank_connections",
                sql: "erased_at IS NULL OR (status = 'archived' AND label IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "bank_connections_iban_format",
                table: "bank_connections",
                sql: "iban ~ '^CZ[0-9]{22}$' OR (erased_at IS NOT NULL AND iban ~ '^CZ…[0-9]{4}…[0-9]{2}$')");

            migrationBuilder.CreateIndex(
                name: "tenant_erasures_tenant_unique",
                table: "tenant_erasures",
                column: "tenant_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tenant_erasures");

            migrationBuilder.DropCheckConstraint(
                name: "webhook_events_payload_consistent",
                table: "webhook_events");

            migrationBuilder.DropCheckConstraint(
                name: "webhook_endpoints_erasure_consistent",
                table: "webhook_endpoints");

            migrationBuilder.DropCheckConstraint(
                name: "tenants_erasure_consistent",
                table: "tenants");

            migrationBuilder.DropCheckConstraint(
                name: "payment_requests_erasure_consistent",
                table: "payment_requests");

            migrationBuilder.DropCheckConstraint(
                name: "payment_requests_iban_format",
                table: "payment_requests");

            migrationBuilder.DropCheckConstraint(
                name: "inbound_messages_content_consistent",
                table: "inbound_messages");

            migrationBuilder.DropCheckConstraint(
                name: "inbound_messages_content_state_valid",
                table: "inbound_messages");

            migrationBuilder.DropCheckConstraint(
                name: "inbound_messages_foreign_minimal",
                table: "inbound_messages");

            migrationBuilder.DropCheckConstraint(
                name: "forwarding_sources_address_normalized",
                table: "forwarding_sources");

            migrationBuilder.DropCheckConstraint(
                name: "bank_connections_erased_archived",
                table: "bank_connections");

            migrationBuilder.DropCheckConstraint(
                name: "bank_connections_iban_format",
                table: "bank_connections");

            migrationBuilder.DropColumn(
                name: "payload_erased_at",
                table: "webhook_events");

            migrationBuilder.DropColumn(
                name: "erased_at",
                table: "webhook_endpoints");

            migrationBuilder.DropColumn(
                name: "erased_at",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "erasure_requested_at",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "erased_at",
                table: "payment_requests");

            migrationBuilder.DropColumn(
                name: "purged_at",
                table: "message_parse_runs");

            migrationBuilder.DropColumn(
                name: "erased_at",
                table: "incoming_transactions");

            migrationBuilder.DropColumn(
                name: "payer_data_purged_at",
                table: "incoming_transactions");

            migrationBuilder.DropColumn(
                name: "erased_at",
                table: "forwarding_sources");

            migrationBuilder.DropColumn(
                name: "erased_at",
                table: "bank_connections");

            migrationBuilder.AlterColumn<string>(
                name: "body",
                table: "webhook_events",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "url",
                table: "webhook_endpoints",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(2000)",
                oldMaxLength: 2000,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "url",
                table: "webhook_destination_revisions",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(2000)",
                oldMaxLength: 2000,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "url",
                table: "webhook_delivery_attempts",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(2000)",
                oldMaxLength: 2000,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "qr_payload",
                table: "payment_requests",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(1000)",
                oldMaxLength: 1000,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "response_body",
                table: "manual_command_receipts",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "address",
                table: "forwarding_sources",
                type: "character varying(254)",
                maxLength: 254,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(254)",
                oldMaxLength: 254,
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "payment_requests_iban_format",
                table: "payment_requests",
                sql: "iban ~ '^CZ[0-9]{22}$'");

            migrationBuilder.AddCheckConstraint(
                name: "inbound_messages_content_consistent",
                table: "inbound_messages",
                sql: "(content_state = 'available' AND content_sha256 IS NOT NULL AND content_stored_at IS NOT NULL) OR (content_state = 'pending' AND content_sha256 IS NULL AND content_stored_at IS NULL) OR (content_state = 'failed' AND content_sha256 IS NULL AND content_stored_at IS NULL AND fetch_error_code IS NOT NULL) OR (content_state = 'skipped' AND content_sha256 IS NULL AND content_stored_at IS NULL AND text_body IS NULL AND html_body IS NULL AND headers IS NULL AND attachments IS NULL AND raw_provider_content IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "inbound_messages_content_state_valid",
                table: "inbound_messages",
                sql: "content_state IN ('pending', 'available', 'failed', 'skipped')");

            migrationBuilder.AddCheckConstraint(
                name: "inbound_messages_foreign_minimal",
                table: "inbound_messages",
                sql: "(sender_class IS DISTINCT FROM 'foreign' AND hold_reason IS DISTINCT FROM 'address_paused') OR (content_state = 'skipped' AND subject IS NULL AND header_from IS NULL AND envelope_sender IS NULL AND transport_metadata IS NULL AND retain_until IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "forwarding_sources_address_normalized",
                table: "forwarding_sources",
                sql: "address = lower(btrim(address)) AND address LIKE '_%@_%'");

            migrationBuilder.AddCheckConstraint(
                name: "bank_connections_iban_format",
                table: "bank_connections",
                sql: "iban ~ '^CZ[0-9]{22}$'");
        }
    }
}
