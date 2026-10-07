using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pairrot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ForwardedNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "inbound_messages_content_consistent",
                table: "inbound_messages");

            migrationBuilder.DropCheckConstraint(
                name: "inbound_messages_content_state_valid",
                table: "inbound_messages");

            migrationBuilder.DropCheckConstraint(
                name: "inbound_messages_hold_reason_valid",
                table: "inbound_messages");

            migrationBuilder.AddColumn<string>(
                name: "origin_path",
                table: "incoming_transactions",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "direct");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "retain_until",
                table: "inbound_messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "sender_class",
                table: "inbound_messages",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "sender_domain",
                table: "inbound_messages",
                type: "character varying(253)",
                maxLength: 253,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_resumed_at",
                table: "inbound_addresses",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "operator_resume_required",
                table: "inbound_addresses",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "paused_at",
                table: "inbound_addresses",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "processing",
                table: "inbound_addresses",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "active");

            migrationBuilder.AddColumn<int>(
                name: "resume_count",
                table: "inbound_addresses",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "forwarding_sources",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bank_connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    address = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    state = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    removed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_forwarding_sources", x => x.id);
                    table.CheckConstraint("forwarding_sources_address_normalized", "address = lower(btrim(address)) AND address LIKE '_%@_%'");
                    table.CheckConstraint("forwarding_sources_state_consistent", "(state = 'active' AND removed_at IS NULL) OR (state = 'removed' AND removed_at IS NOT NULL)");
                    table.CheckConstraint("forwarding_sources_state_valid", "state IN ('active', 'removed')");
                    table.ForeignKey(
                        name: "fk_forwarding_sources_bank_connections_tenant_id_bank_connecti",
                        columns: x => new { x.tenant_id, x.bank_connection_id },
                        principalTable: "bank_connections",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "incoming_transactions_origin_path_valid",
                table: "incoming_transactions",
                sql: "origin_path IN ('direct', 'automatic_forward', 'manual_forward')");

            migrationBuilder.CreateIndex(
                name: "inbound_messages_foreign_idx",
                table: "inbound_messages",
                columns: new[] { "inbound_address_id", "created_at" },
                filter: "sender_class = 'foreign'");

            migrationBuilder.CreateIndex(
                name: "inbound_messages_retention_idx",
                table: "inbound_messages",
                column: "retain_until",
                filter: "retain_until IS NOT NULL");

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
                name: "inbound_messages_hold_reason_valid",
                table: "inbound_messages",
                sql: "hold_reason IS NULL OR hold_reason IN ('address_retired', 'bank_connection_archived', 'tenant_not_active', 'recipient_mismatch', 'address_paused')");

            migrationBuilder.AddCheckConstraint(
                name: "inbound_messages_sender_class_valid",
                table: "inbound_messages",
                sql: "sender_class IS NULL OR sender_class IN ('bank', 'forwarding_confirmation', 'tenant_forward', 'foreign')");

            migrationBuilder.AddCheckConstraint(
                name: "inbound_addresses_pause_consistent",
                table: "inbound_addresses",
                sql: "(processing = 'paused_foreign_mail' AND paused_at IS NOT NULL) OR (processing = 'active' AND NOT operator_resume_required)");

            migrationBuilder.AddCheckConstraint(
                name: "inbound_addresses_processing_valid",
                table: "inbound_addresses",
                sql: "processing IN ('active', 'paused_foreign_mail')");

            migrationBuilder.AddCheckConstraint(
                name: "inbound_addresses_resume_count_valid",
                table: "inbound_addresses",
                sql: "resume_count >= 0");

            migrationBuilder.CreateIndex(
                name: "forwarding_sources_connection_address_unique",
                table: "forwarding_sources",
                columns: new[] { "bank_connection_id", "address" },
                unique: true,
                filter: "state <> 'removed'");

            migrationBuilder.CreateIndex(
                name: "ix_forwarding_sources_tenant_id_bank_connection_id",
                table: "forwarding_sources",
                columns: new[] { "tenant_id", "bank_connection_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "forwarding_sources");

            migrationBuilder.DropCheckConstraint(
                name: "incoming_transactions_origin_path_valid",
                table: "incoming_transactions");

            migrationBuilder.DropIndex(
                name: "inbound_messages_foreign_idx",
                table: "inbound_messages");

            migrationBuilder.DropIndex(
                name: "inbound_messages_retention_idx",
                table: "inbound_messages");

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
                name: "inbound_messages_hold_reason_valid",
                table: "inbound_messages");

            migrationBuilder.DropCheckConstraint(
                name: "inbound_messages_sender_class_valid",
                table: "inbound_messages");

            migrationBuilder.DropCheckConstraint(
                name: "inbound_addresses_pause_consistent",
                table: "inbound_addresses");

            migrationBuilder.DropCheckConstraint(
                name: "inbound_addresses_processing_valid",
                table: "inbound_addresses");

            migrationBuilder.DropCheckConstraint(
                name: "inbound_addresses_resume_count_valid",
                table: "inbound_addresses");

            migrationBuilder.DropColumn(
                name: "origin_path",
                table: "incoming_transactions");

            migrationBuilder.DropColumn(
                name: "retain_until",
                table: "inbound_messages");

            migrationBuilder.DropColumn(
                name: "sender_class",
                table: "inbound_messages");

            migrationBuilder.DropColumn(
                name: "sender_domain",
                table: "inbound_messages");

            migrationBuilder.DropColumn(
                name: "last_resumed_at",
                table: "inbound_addresses");

            migrationBuilder.DropColumn(
                name: "operator_resume_required",
                table: "inbound_addresses");

            migrationBuilder.DropColumn(
                name: "paused_at",
                table: "inbound_addresses");

            migrationBuilder.DropColumn(
                name: "processing",
                table: "inbound_addresses");

            migrationBuilder.DropColumn(
                name: "resume_count",
                table: "inbound_addresses");

            migrationBuilder.AddCheckConstraint(
                name: "inbound_messages_content_consistent",
                table: "inbound_messages",
                sql: "(content_state = 'available' AND content_sha256 IS NOT NULL AND content_stored_at IS NOT NULL) OR (content_state = 'pending' AND content_sha256 IS NULL AND content_stored_at IS NULL) OR (content_state = 'failed' AND content_sha256 IS NULL AND content_stored_at IS NULL AND fetch_error_code IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "inbound_messages_content_state_valid",
                table: "inbound_messages",
                sql: "content_state IN ('pending', 'available', 'failed')");

            migrationBuilder.AddCheckConstraint(
                name: "inbound_messages_hold_reason_valid",
                table: "inbound_messages",
                sql: "hold_reason IS NULL OR hold_reason IN ('address_retired', 'bank_connection_archived', 'tenant_not_active', 'recipient_mismatch')");
        }
    }
}
