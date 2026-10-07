using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pairrot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InboundEmailReception : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "background_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    generation = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    lease_owner = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    lease_token = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    last_error_message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    requested_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_background_jobs", x => x.id);
                    table.CheckConstraint("background_jobs_completion_consistent", "(state IN ('succeeded', 'failed') AND completed_at IS NOT NULL) OR (state NOT IN ('succeeded', 'failed') AND completed_at IS NULL)");
                    table.CheckConstraint("background_jobs_counts_valid", "generation >= 1 AND attempt_count >= 0");
                    table.CheckConstraint("background_jobs_lease_consistent", "(state = 'running' AND lease_token IS NOT NULL AND lease_expires_at IS NOT NULL AND lease_owner IS NOT NULL) OR (state <> 'running' AND lease_token IS NULL AND lease_expires_at IS NULL AND lease_owner IS NULL)");
                    table.CheckConstraint("background_jobs_state_valid", "state IN ('queued', 'running', 'retry_wait', 'succeeded', 'failed')");
                });

            migrationBuilder.CreateTable(
                name: "inbound_addresses",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bank_connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    address = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    provider_instance_id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    provider_kind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    environment = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    provisioning_state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    provisioning_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ready_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    retired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inbound_addresses", x => x.id);
                    table.UniqueConstraint("ak_inbound_addresses_id_tenant_id_bank_connection_id", x => new { x.id, x.tenant_id, x.bank_connection_id });
                    table.CheckConstraint("inbound_addresses_address_normalized", "address = lower(btrim(address)) AND address LIKE '_%@_%'");
                    table.CheckConstraint("inbound_addresses_environment_valid", "environment IN ('test', 'live')");
                    table.CheckConstraint("inbound_addresses_live_not_on_test_domain", "environment = 'test' OR address !~ '@([a-z0-9-]+\\.)*[a-z0-9-]+\\.(test|example|invalid|localhost)$'");
                    table.CheckConstraint("inbound_addresses_provisioning_consistent", "(provisioning_state = 'ready' AND ready_at IS NOT NULL) OR (provisioning_state <> 'ready' AND ready_at IS NULL)");
                    table.CheckConstraint("inbound_addresses_provisioning_valid", "provisioning_state IN ('pending', 'ready', 'failed')");
                    table.CheckConstraint("inbound_addresses_retirement_consistent", "(state = 'retired' AND retired_at IS NOT NULL) OR (state = 'active' AND retired_at IS NULL)");
                    table.CheckConstraint("inbound_addresses_state_valid", "state IN ('active', 'retired')");
                    table.ForeignKey(
                        name: "fk_inbound_addresses_bank_connections_tenant_id_bank_connectio",
                        columns: x => new { x.tenant_id, x.bank_connection_id },
                        principalTable: "bank_connections",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_inbound_addresses_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "inbound_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_instance_id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    source_message_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    source_kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    environment = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    routing_state = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    bank_connection_id = table.Column<Guid>(type: "uuid", nullable: true),
                    inbound_address_id = table.Column<Guid>(type: "uuid", nullable: true),
                    hold_reason = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    envelope_recipients = table.Column<List<string>>(type: "text[]", nullable: false),
                    recipient_evidence = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    envelope_sender = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    header_from = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    subject = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    transport_kind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    delivery_evidence = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    transport_metadata = table.Column<string>(type: "text", nullable: true),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    content_mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    content_reference = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    content_state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    content_stored_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    content_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    text_body = table.Column<string>(type: "text", nullable: true),
                    html_body = table.Column<string>(type: "text", nullable: true),
                    headers = table.Column<string>(type: "text", nullable: true),
                    content_recipients = table.Column<List<string>>(type: "text[]", nullable: true),
                    attachments = table.Column<string>(type: "text", nullable: true),
                    raw_provider_content = table.Column<string>(type: "text", nullable: true),
                    spf_result = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    dkim_result = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    dmarc_result = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    authentication_origin = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    authentication_raw = table.Column<string>(type: "text", nullable: true),
                    fetch_error_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    fetch_error_message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    fetch_error_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inbound_messages", x => x.id);
                    table.CheckConstraint("inbound_messages_authentication_valid", "spf_result IN ('fail', 'neutral', 'none', 'pass', 'permerror', 'policy', 'softfail', 'temperror', 'unknown') AND dkim_result IN ('fail', 'neutral', 'none', 'pass', 'permerror', 'policy', 'softfail', 'temperror', 'unknown') AND dmarc_result IN ('fail', 'neutral', 'none', 'pass', 'permerror', 'policy', 'softfail', 'temperror', 'unknown')");
                    table.CheckConstraint("inbound_messages_content_consistent", "(content_state = 'available' AND content_sha256 IS NOT NULL AND content_stored_at IS NOT NULL) OR (content_state = 'pending' AND content_sha256 IS NULL AND content_stored_at IS NULL) OR (content_state = 'failed' AND content_sha256 IS NULL AND content_stored_at IS NULL AND fetch_error_code IS NOT NULL)");
                    table.CheckConstraint("inbound_messages_content_digest_format", "content_sha256 IS NULL OR content_sha256 ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("inbound_messages_content_mode_consistent", "(content_mode = 'deferred' AND content_reference IS NOT NULL) OR (content_mode = 'inline' AND content_reference IS NULL)");
                    table.CheckConstraint("inbound_messages_content_state_valid", "content_state IN ('pending', 'available', 'failed')");
                    table.CheckConstraint("inbound_messages_environment_valid", "environment IN ('test', 'live')");
                    table.CheckConstraint("inbound_messages_hold_reason_valid", "hold_reason IS NULL OR hold_reason IN ('address_retired', 'bank_connection_archived', 'tenant_not_active', 'recipient_mismatch')");
                    table.CheckConstraint("inbound_messages_routing_consistent", "(routing_state = 'routed' AND tenant_id IS NOT NULL AND bank_connection_id IS NOT NULL AND inbound_address_id IS NOT NULL) OR (routing_state <> 'routed' AND tenant_id IS NULL AND bank_connection_id IS NULL AND inbound_address_id IS NULL AND hold_reason IS NULL)");
                    table.CheckConstraint("inbound_messages_routing_state_valid", "routing_state IN ('routed', 'unknown_recipient', 'ambiguous_recipient', 'instance_mismatch', 'missing_recipient_evidence')");
                    table.CheckConstraint("inbound_messages_source_kind_valid", "source_kind IN ('received', 'fixture')");
                    table.ForeignKey(
                        name: "fk_inbound_messages_bank_connections_tenant_id_bank_connection",
                        columns: x => new { x.tenant_id, x.bank_connection_id },
                        principalTable: "bank_connections",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_inbound_messages_inbound_addresses_inbound_address_id_tenan",
                        columns: x => new { x.inbound_address_id, x.tenant_id, x.bank_connection_id },
                        principalTable: "inbound_addresses",
                        principalColumns: new[] { "id", "tenant_id", "bank_connection_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_inbound_messages_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "inbound_delivery_receipts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_instance_id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    source_delivery_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    transport_kind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    delivery_evidence = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    raw_payload = table.Column<string>(type: "text", nullable: true),
                    payload_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    outcome = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    inbound_message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inbound_delivery_receipts", x => x.id);
                    table.CheckConstraint("inbound_delivery_receipts_digest_format", "payload_sha256 ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("inbound_delivery_receipts_outcome_valid", "outcome IN ('message_created', 'existing_message')");
                    table.ForeignKey(
                        name: "fk_inbound_delivery_receipts_inbound_messages_inbound_message_",
                        column: x => x.inbound_message_id,
                        principalTable: "inbound_messages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "background_jobs_due_idx",
                table: "background_jobs",
                column: "due_at",
                filter: "state IN ('queued', 'running', 'retry_wait')");

            migrationBuilder.CreateIndex(
                name: "background_jobs_generation_unique",
                table: "background_jobs",
                columns: new[] { "type", "target_id", "generation" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "background_jobs_one_active",
                table: "background_jobs",
                columns: new[] { "type", "target_id" },
                unique: true,
                filter: "state IN ('queued', 'running', 'retry_wait')");

            migrationBuilder.CreateIndex(
                name: "inbound_addresses_address_unique",
                table: "inbound_addresses",
                column: "address",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "inbound_addresses_one_active_per_connection",
                table: "inbound_addresses",
                column: "bank_connection_id",
                unique: true,
                filter: "state = 'active'");

            migrationBuilder.CreateIndex(
                name: "ix_inbound_addresses_tenant_id_bank_connection_id",
                table: "inbound_addresses",
                columns: new[] { "tenant_id", "bank_connection_id" });

            migrationBuilder.CreateIndex(
                name: "inbound_delivery_receipts_delivery_unique",
                table: "inbound_delivery_receipts",
                columns: new[] { "provider_instance_id", "source_delivery_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "inbound_delivery_receipts_message_idx",
                table: "inbound_delivery_receipts",
                column: "inbound_message_id");

            migrationBuilder.CreateIndex(
                name: "inbound_messages_connection_idx",
                table: "inbound_messages",
                columns: new[] { "bank_connection_id", "received_at" });

            migrationBuilder.CreateIndex(
                name: "inbound_messages_quarantine_idx",
                table: "inbound_messages",
                column: "routing_state",
                filter: "routing_state <> 'routed'");

            migrationBuilder.CreateIndex(
                name: "inbound_messages_source_message_unique",
                table: "inbound_messages",
                columns: new[] { "provider_instance_id", "source_message_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "inbound_messages_tenant_idx",
                table: "inbound_messages",
                columns: new[] { "tenant_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_inbound_messages_inbound_address_id_tenant_id_bank_connecti",
                table: "inbound_messages",
                columns: new[] { "inbound_address_id", "tenant_id", "bank_connection_id" });

            migrationBuilder.CreateIndex(
                name: "ix_inbound_messages_tenant_id_bank_connection_id",
                table: "inbound_messages",
                columns: new[] { "tenant_id", "bank_connection_id" });

            // Every Feature 003 test address moves into inbound_addresses, bound to the 'stub' instance,
            // with its value and owner unchanged, before the old columns are dropped. It stays a test address.
            migrationBuilder.Sql("""
                INSERT INTO inbound_addresses (id, tenant_id, bank_connection_id, address, provider_instance_id, provider_kind, environment,
                    provisioning_state, state, created_at, ready_at)
                SELECT gen_random_uuid(), tenant_id, id, lower(inbound_address), 'stub', 'stub', 'test', 'ready', 'active',
                    address_created_at, address_created_at
                FROM bank_connections
                WHERE inbound_address IS NOT NULL;
                """);

            migrationBuilder.DropIndex(
                name: "bank_connections_inbound_address_unique",
                table: "bank_connections");

            migrationBuilder.DropCheckConstraint(
                name: "bank_connections_address_consistent",
                table: "bank_connections");

            migrationBuilder.DropCheckConstraint(
                name: "bank_connections_address_status_valid",
                table: "bank_connections");

            migrationBuilder.DropColumn(
                name: "address_created_at",
                table: "bank_connections");

            migrationBuilder.DropColumn(
                name: "address_status",
                table: "bank_connections");

            migrationBuilder.DropColumn(
                name: "email_provider",
                table: "bank_connections");

            migrationBuilder.DropColumn(
                name: "inbound_address",
                table: "bank_connections");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "address_created_at",
                table: "bank_connections",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "address_status",
                table: "bank_connections",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "not_created");

            migrationBuilder.AddColumn<string>(
                name: "email_provider",
                table: "bank_connections",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "inbound_address",
                table: "bank_connections",
                type: "character varying(254)",
                maxLength: 254,
                nullable: true);

            // Only active stub addresses fit the Feature 003 columns; live addresses and all received mail are lost.
            migrationBuilder.Sql("""
                UPDATE bank_connections b
                SET inbound_address = a.address, email_provider = 'stub', address_status = 'stub_created', address_created_at = a.created_at
                FROM inbound_addresses a
                WHERE a.bank_connection_id = b.id AND a.state = 'active' AND a.provider_kind = 'stub' AND a.provisioning_state = 'ready';
                """);

            migrationBuilder.DropTable(
                name: "background_jobs");

            migrationBuilder.DropTable(
                name: "inbound_delivery_receipts");

            migrationBuilder.DropTable(
                name: "inbound_messages");

            migrationBuilder.DropTable(
                name: "inbound_addresses");

            migrationBuilder.CreateIndex(
                name: "bank_connections_inbound_address_unique",
                table: "bank_connections",
                column: "inbound_address",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "bank_connections_address_consistent",
                table: "bank_connections",
                sql: "(address_status = 'not_created' AND inbound_address IS NULL AND address_created_at IS NULL AND email_provider IS NULL) OR (address_status = 'stub_created' AND inbound_address IS NOT NULL AND address_created_at IS NOT NULL AND email_provider = 'stub')");

            migrationBuilder.AddCheckConstraint(
                name: "bank_connections_address_status_valid",
                table: "bank_connections",
                sql: "address_status IN ('not_created', 'stub_created')");
        }
    }
}
