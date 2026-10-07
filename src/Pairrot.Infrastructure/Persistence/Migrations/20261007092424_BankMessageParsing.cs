using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pairrot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BankMessageParsing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "latest_parse_outcome",
                table: "inbound_messages",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "latest_parse_run_id",
                table: "inbound_messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "parsed_at",
                table: "inbound_messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "incoming_transactions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bank_connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    variable_symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    variable_symbol_key = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    bank_transaction_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    sender_account = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    sender_account_raw = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    sender_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    booked_on = table.Column<DateOnly>(type: "date", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    bank_profile = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    parser_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ingestion_status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ingestion_reasons = table.Column<string>(type: "text", nullable: false),
                    comparison_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    source_kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    environment = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_incoming_transactions", x => x.id);
                    table.UniqueConstraint("ak_incoming_transactions_id_tenant_id_bank_connection_id", x => new { x.id, x.tenant_id, x.bank_connection_id });
                    table.CheckConstraint("incoming_transactions_amount_positive", "amount > 0");
                    table.CheckConstraint("incoming_transactions_currency_valid", "currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("incoming_transactions_environment_valid", "environment IN ('test', 'live')");
                    table.CheckConstraint("incoming_transactions_fingerprint_format", "comparison_fingerprint ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("incoming_transactions_source_kind_valid", "source_kind IN ('received', 'fixture')");
                    table.CheckConstraint("incoming_transactions_status_valid", "ingestion_status IN ('accepted', 'review_required', 'disputed')");
                    table.CheckConstraint("incoming_transactions_symbol_key_consistent", "(variable_symbol IS NULL AND variable_symbol_key IS NULL) OR (variable_symbol IS NOT NULL AND variable_symbol_key IS NOT NULL)");
                    table.CheckConstraint("incoming_transactions_symbol_valid", "variable_symbol IS NULL OR variable_symbol ~ '^[0-9]{1,10}$'");
                    table.ForeignKey(
                        name: "fk_incoming_transactions_bank_connections_tenant_id_bank_conne",
                        columns: x => new { x.tenant_id, x.bank_connection_id },
                        principalTable: "bank_connections",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "message_parse_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    inbound_message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bank_connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_generation = table.Column<int>(type: "integer", nullable: false),
                    source_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    bank_profile = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    parser_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    trust_decision = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    trust_evidence = table.Column<string>(type: "text", nullable: true),
                    outcome = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    reason_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    reason_message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    reasons = table.Column<string>(type: "text", nullable: false),
                    template_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    source_part = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    candidate = table.Column<string>(type: "text", nullable: true),
                    transaction_id = table.Column<Guid>(type: "uuid", nullable: true),
                    requested_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    worker = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_message_parse_runs", x => x.id);
                    table.UniqueConstraint("ak_message_parse_runs_id_tenant_id_bank_connection_id", x => new { x.id, x.tenant_id, x.bank_connection_id });
                    table.CheckConstraint("message_parse_runs_digest_format", "source_sha256 ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("message_parse_runs_generation_valid", "job_generation >= 1");
                    table.CheckConstraint("message_parse_runs_outcome_valid", "outcome IN ('parsed', 'duplicate', 'not_payment', 'unsupported', 'review_required', 'failed')");
                    table.CheckConstraint("message_parse_runs_part_valid", "source_part IS NULL OR source_part IN ('text', 'html')");
                    table.CheckConstraint("message_parse_runs_times_valid", "completed_at >= started_at");
                    table.CheckConstraint("message_parse_runs_transaction_consistent", "outcome NOT IN ('parsed', 'duplicate') OR transaction_id IS NOT NULL");
                    table.CheckConstraint("message_parse_runs_trust_valid", "trust_decision IS NULL OR trust_decision IN ('trusted', 'untrusted')");
                    table.ForeignKey(
                        name: "fk_message_parse_runs_bank_connections_tenant_id_bank_connecti",
                        columns: x => new { x.tenant_id, x.bank_connection_id },
                        principalTable: "bank_connections",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_message_parse_runs_incoming_transactions_transaction_id_ten",
                        columns: x => new { x.transaction_id, x.tenant_id, x.bank_connection_id },
                        principalTable: "incoming_transactions",
                        principalColumns: new[] { "id", "tenant_id", "bank_connection_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "transaction_sources",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    inbound_message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    parse_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bank_connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_transaction_sources", x => x.id);
                    table.ForeignKey(
                        name: "fk_transaction_sources_incoming_transactions_transaction_id_te",
                        columns: x => new { x.transaction_id, x.tenant_id, x.bank_connection_id },
                        principalTable: "incoming_transactions",
                        principalColumns: new[] { "id", "tenant_id", "bank_connection_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_transaction_sources_message_parse_runs_parse_run_id_tenant_",
                        columns: x => new { x.parse_run_id, x.tenant_id, x.bank_connection_id },
                        principalTable: "message_parse_runs",
                        principalColumns: new[] { "id", "tenant_id", "bank_connection_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_inbound_messages_latest_parse_run_id",
                table: "inbound_messages",
                column: "latest_parse_run_id");

            migrationBuilder.AddCheckConstraint(
                name: "inbound_messages_parse_consistent",
                table: "inbound_messages",
                sql: "(latest_parse_run_id IS NULL AND latest_parse_outcome IS NULL AND parsed_at IS NULL) OR (latest_parse_run_id IS NOT NULL AND latest_parse_outcome IN ('parsed', 'duplicate', 'not_payment', 'unsupported', 'review_required', 'failed') AND parsed_at IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "incoming_transactions_bank_reference_unique",
                table: "incoming_transactions",
                columns: new[] { "bank_connection_id", "bank_profile", "bank_transaction_id" },
                unique: true,
                filter: "bank_transaction_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "incoming_transactions_fingerprint_idx",
                table: "incoming_transactions",
                columns: new[] { "bank_connection_id", "comparison_fingerprint" });

            migrationBuilder.CreateIndex(
                name: "incoming_transactions_tenant_idx",
                table: "incoming_transactions",
                columns: new[] { "tenant_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_incoming_transactions_tenant_id_bank_connection_id",
                table: "incoming_transactions",
                columns: new[] { "tenant_id", "bank_connection_id" });

            migrationBuilder.CreateIndex(
                name: "ix_message_parse_runs_tenant_id_bank_connection_id",
                table: "message_parse_runs",
                columns: new[] { "tenant_id", "bank_connection_id" });

            migrationBuilder.CreateIndex(
                name: "ix_message_parse_runs_transaction_id_tenant_id_bank_connection",
                table: "message_parse_runs",
                columns: new[] { "transaction_id", "tenant_id", "bank_connection_id" });

            migrationBuilder.CreateIndex(
                name: "message_parse_runs_message_generation_unique",
                table: "message_parse_runs",
                columns: new[] { "inbound_message_id", "job_generation" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_transaction_sources_parse_run_id_tenant_id_bank_connection_",
                table: "transaction_sources",
                columns: new[] { "parse_run_id", "tenant_id", "bank_connection_id" });

            migrationBuilder.CreateIndex(
                name: "ix_transaction_sources_transaction_id_tenant_id_bank_connectio",
                table: "transaction_sources",
                columns: new[] { "transaction_id", "tenant_id", "bank_connection_id" });

            migrationBuilder.CreateIndex(
                name: "transaction_sources_message_unique",
                table: "transaction_sources",
                column: "inbound_message_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "transaction_sources_transaction_idx",
                table: "transaction_sources",
                column: "transaction_id");

            migrationBuilder.AddForeignKey(
                name: "fk_inbound_messages_message_parse_runs_latest_parse_run_id",
                table: "inbound_messages",
                column: "latest_parse_run_id",
                principalTable: "message_parse_runs",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            // Owner-consistent references to messages, whose owner columns are nullable (so not an EF alternate key):
            // runs and source links belong to the message's tenant and connection, a source link's run read the
            // same message, and a message's latest run is one of its own runs.
            migrationBuilder.Sql("""
                ALTER TABLE inbound_messages ADD CONSTRAINT inbound_messages_owner_key UNIQUE (id, tenant_id, bank_connection_id);
                ALTER TABLE message_parse_runs ADD CONSTRAINT message_parse_runs_message_key UNIQUE (id, inbound_message_id);
                ALTER TABLE message_parse_runs ADD CONSTRAINT fk_message_parse_runs_inbound_messages_owner
                    FOREIGN KEY (inbound_message_id, tenant_id, bank_connection_id)
                    REFERENCES inbound_messages (id, tenant_id, bank_connection_id) ON DELETE RESTRICT;
                ALTER TABLE transaction_sources ADD CONSTRAINT fk_transaction_sources_inbound_messages_owner
                    FOREIGN KEY (inbound_message_id, tenant_id, bank_connection_id)
                    REFERENCES inbound_messages (id, tenant_id, bank_connection_id) ON DELETE RESTRICT;
                ALTER TABLE transaction_sources ADD CONSTRAINT fk_transaction_sources_run_of_message
                    FOREIGN KEY (parse_run_id, inbound_message_id)
                    REFERENCES message_parse_runs (id, inbound_message_id) ON DELETE RESTRICT;
                ALTER TABLE inbound_messages ADD CONSTRAINT fk_inbound_messages_latest_run_of_message
                    FOREIGN KEY (latest_parse_run_id, id)
                    REFERENCES message_parse_runs (id, inbound_message_id) ON DELETE RESTRICT;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE inbound_messages DROP CONSTRAINT fk_inbound_messages_latest_run_of_message;");

            migrationBuilder.DropForeignKey(
                name: "fk_inbound_messages_message_parse_runs_latest_parse_run_id",
                table: "inbound_messages");

            migrationBuilder.DropTable(
                name: "transaction_sources");

            migrationBuilder.DropTable(
                name: "message_parse_runs");

            migrationBuilder.DropTable(
                name: "incoming_transactions");

            migrationBuilder.Sql("ALTER TABLE inbound_messages DROP CONSTRAINT inbound_messages_owner_key;");

            migrationBuilder.DropIndex(
                name: "ix_inbound_messages_latest_parse_run_id",
                table: "inbound_messages");

            migrationBuilder.DropCheckConstraint(
                name: "inbound_messages_parse_consistent",
                table: "inbound_messages");

            migrationBuilder.DropColumn(
                name: "latest_parse_outcome",
                table: "inbound_messages");

            migrationBuilder.DropColumn(
                name: "latest_parse_run_id",
                table: "inbound_messages");

            migrationBuilder.DropColumn(
                name: "parsed_at",
                table: "inbound_messages");
        }
    }
}
