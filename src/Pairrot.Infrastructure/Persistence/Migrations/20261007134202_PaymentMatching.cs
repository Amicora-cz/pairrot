using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pairrot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PaymentMatching : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "payment_requests_status_valid",
                table: "payment_requests");

            migrationBuilder.AddColumn<int>(
                name: "change_sequence",
                table: "payment_requests",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "paid_amount",
                table: "payment_requests",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "auto_match_block_reason",
                table: "incoming_transactions",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "auto_match_blocked",
                table: "incoming_transactions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "canonical_transaction_id",
                table: "incoming_transactions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "confirmation_decision_id",
                table: "incoming_transactions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "handled_at",
                table: "incoming_transactions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "handled_by",
                table: "incoming_transactions",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "handling",
                table: "incoming_transactions",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "unallocated");

            migrationBuilder.AddColumn<string>(
                name: "handling_reason",
                table: "incoming_transactions",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "manually_confirmed",
                table: "incoming_transactions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "match_checked_at",
                table: "incoming_transactions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "match_request_id",
                table: "incoming_transactions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "match_result",
                table: "incoming_transactions",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "version",
                table: "incoming_transactions",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddUniqueConstraint(
                name: "ak_payment_requests_id_tenant_id_bank_connection_id",
                table: "payment_requests",
                columns: new[] { "id", "tenant_id", "bank_connection_id" });

            migrationBuilder.CreateTable(
                name: "manual_command_receipts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    action = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    response_status = table.Column<int>(type: "integer", nullable: false),
                    response_body = table.Column<string>(type: "text", nullable: false),
                    resource_ids = table.Column<string>(type: "text", nullable: false),
                    actor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_manual_command_receipts", x => x.id);
                    table.CheckConstraint("manual_command_receipts_fingerprint_format", "fingerprint ~ '^[0-9a-f]{64}$'");
                    table.ForeignKey(
                        name: "fk_manual_command_receipts_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "payment_allocations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bank_connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    method = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    evidence = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    revocation_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    revocation_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_allocations", x => x.id);
                    table.CheckConstraint("payment_allocations_amount_positive", "amount > 0");
                    table.CheckConstraint("payment_allocations_currency_valid", "currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("payment_allocations_evidence_valid", "evidence IN ('live', 'test', 'fixture')");
                    table.CheckConstraint("payment_allocations_manual_reason", "method = 'automatic' OR btrim(coalesce(reason, '')) <> ''");
                    table.CheckConstraint("payment_allocations_method_valid", "method IN ('automatic', 'manual')");
                    table.CheckConstraint("payment_allocations_revocation_consistent", "(state = 'active' AND revoked_at IS NULL AND revoked_by IS NULL AND revocation_code IS NULL) OR (state = 'revoked' AND revoked_at IS NOT NULL AND revoked_by IS NOT NULL AND revocation_code IS NOT NULL)");
                    table.CheckConstraint("payment_allocations_state_valid", "state IN ('active', 'revoked')");
                    table.CheckConstraint("payment_allocations_version_positive", "version >= 1");
                    table.ForeignKey(
                        name: "fk_payment_allocations_incoming_transactions_transaction_id_te",
                        columns: x => new { x.transaction_id, x.tenant_id, x.bank_connection_id },
                        principalTable: "incoming_transactions",
                        principalColumns: new[] { "id", "tenant_id", "bank_connection_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payment_allocations_payment_requests_payment_request_id_ten",
                        columns: x => new { x.payment_request_id, x.tenant_id, x.bank_connection_id },
                        principalTable: "payment_requests",
                        principalColumns: new[] { "id", "tenant_id", "bank_connection_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "payment_review_cases",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bank_connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    issue = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    inbound_message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    reason_message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    evidence_key = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    resolution = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_review_cases", x => x.id);
                    table.UniqueConstraint("ak_payment_review_cases_id_tenant_id_bank_connection_id", x => new { x.id, x.tenant_id, x.bank_connection_id });
                    table.CheckConstraint("payment_review_cases_issue_valid", "issue IN ('unverified_candidate', 'possible_duplicate', 'disputed')");
                    table.CheckConstraint("payment_review_cases_message_consistent", "(issue = 'unverified_candidate') = (inbound_message_id IS NOT NULL)");
                    table.CheckConstraint("payment_review_cases_resolution_valid", "resolution IS NULL OR resolution IN ('confirm_payment', 'confirm_distinct', 'mark_duplicate', 'keep_existing', 'reject', 'reopen', 'superseded')");
                    table.CheckConstraint("payment_review_cases_resolved_consistent", "(state = 'resolved' AND resolved_at IS NOT NULL AND resolution IS NOT NULL) OR state = 'open'");
                    table.CheckConstraint("payment_review_cases_state_valid", "state IN ('open', 'resolved')");
                    table.CheckConstraint("payment_review_cases_version_positive", "version >= 1");
                    table.ForeignKey(
                        name: "fk_payment_review_cases_bank_connections_tenant_id_bank_connec",
                        columns: x => new { x.tenant_id, x.bank_connection_id },
                        principalTable: "bank_connections",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "payment_request_changes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bank_connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    external_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    expected_amount = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    status_before = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    paid_before = table.Column<long>(type: "bigint", nullable: false),
                    remaining_before = table.Column<long>(type: "bigint", nullable: true),
                    overpaid_before = table.Column<long>(type: "bigint", nullable: false),
                    status_after = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    paid_after = table.Column<long>(type: "bigint", nullable: false),
                    remaining_after = table.Column<long>(type: "bigint", nullable: true),
                    overpaid_after = table.Column<long>(type: "bigint", nullable: false),
                    cause = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    allocation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    transaction_id = table.Column<Guid>(type: "uuid", nullable: true),
                    evidence = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    actor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_request_changes", x => x.id);
                    table.CheckConstraint("payment_request_changes_amounts_valid", "paid_before >= 0 AND paid_after >= 0 AND overpaid_before >= 0 AND overpaid_after >= 0");
                    table.CheckConstraint("payment_request_changes_cause_valid", "cause IN ('automatic_allocation', 'manual_allocation', 'allocation_revoked', 'transaction_review_required', 'cancelled')");
                    table.CheckConstraint("payment_request_changes_sequence_positive", "sequence >= 1");
                    table.CheckConstraint("payment_request_changes_status_after_valid", "status_after IN ('open', 'partially_paid', 'paid', 'overpaid', 'cancelled')");
                    table.CheckConstraint("payment_request_changes_status_before_valid", "status_before IN ('open', 'partially_paid', 'paid', 'overpaid', 'cancelled')");
                    table.ForeignKey(
                        name: "fk_payment_request_changes_payment_allocations_allocation_id",
                        column: x => x.allocation_id,
                        principalTable: "payment_allocations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payment_request_changes_payment_requests_payment_request_id",
                        columns: x => new { x.payment_request_id, x.tenant_id, x.bank_connection_id },
                        principalTable: "payment_requests",
                        principalColumns: new[] { "id", "tenant_id", "bank_connection_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "payment_review_case_members",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    case_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bank_connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    transaction_id = table.Column<Guid>(type: "uuid", nullable: true),
                    parse_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_review_case_members", x => x.id);
                    table.CheckConstraint("payment_review_case_members_one_target", "(transaction_id IS NULL) <> (parse_run_id IS NULL)");
                    table.ForeignKey(
                        name: "fk_payment_review_case_members_incoming_transactions_transacti",
                        columns: x => new { x.transaction_id, x.tenant_id, x.bank_connection_id },
                        principalTable: "incoming_transactions",
                        principalColumns: new[] { "id", "tenant_id", "bank_connection_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payment_review_case_members_message_parse_runs_parse_run_id",
                        columns: x => new { x.parse_run_id, x.tenant_id, x.bank_connection_id },
                        principalTable: "message_parse_runs",
                        principalColumns: new[] { "id", "tenant_id", "bank_connection_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payment_review_case_members_payment_review_cases_case_id_te",
                        columns: x => new { x.case_id, x.tenant_id, x.bank_connection_id },
                        principalTable: "payment_review_cases",
                        principalColumns: new[] { "id", "tenant_id", "bank_connection_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "payment_review_decisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    case_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    actor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    case_version = table.Column<int>(type: "integer", nullable: false),
                    members = table.Column<string>(type: "text", nullable: false),
                    canonical_transaction_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_transaction_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_review_decisions", x => x.id);
                    table.CheckConstraint("payment_review_decisions_action_valid", "action IN ('confirm_payment', 'confirm_distinct', 'mark_duplicate', 'keep_existing', 'reject', 'reopen', 'superseded')");
                    table.CheckConstraint("payment_review_decisions_reason_not_blank", "btrim(reason) <> ''");
                    table.ForeignKey(
                        name: "fk_payment_review_decisions_payment_review_cases_case_id",
                        column: x => x.case_id,
                        principalTable: "payment_review_cases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "payment_requests_change_sequence_valid",
                table: "payment_requests",
                sql: "change_sequence >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "payment_requests_paid_consistent",
                table: "payment_requests",
                sql: "(status = 'open' AND paid_amount = 0) OR (status = 'partially_paid' AND paid_amount > 0 AND paid_amount < amount) OR (status = 'paid' AND paid_amount = amount) OR (status = 'overpaid' AND paid_amount > amount) OR (status = 'cancelled' AND paid_amount = 0)");

            migrationBuilder.AddCheckConstraint(
                name: "payment_requests_status_valid",
                table: "payment_requests",
                sql: "status IN ('open', 'partially_paid', 'paid', 'overpaid', 'cancelled')");

            migrationBuilder.CreateIndex(
                name: "incoming_transactions_unallocated_symbol_idx",
                table: "incoming_transactions",
                columns: new[] { "bank_connection_id", "variable_symbol_key" },
                filter: "handling = 'unallocated'");

            migrationBuilder.CreateIndex(
                name: "ix_incoming_transactions_canonical_transaction_id_tenant_id_ba",
                table: "incoming_transactions",
                columns: new[] { "canonical_transaction_id", "tenant_id", "bank_connection_id" });

            migrationBuilder.AddCheckConstraint(
                name: "incoming_transactions_canonical_consistent",
                table: "incoming_transactions",
                sql: "(handling = 'duplicate' AND canonical_transaction_id IS NOT NULL AND canonical_transaction_id <> id) OR (handling <> 'duplicate' AND canonical_transaction_id IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "incoming_transactions_handling_valid",
                table: "incoming_transactions",
                sql: "handling IN ('unallocated', 'allocated', 'ignored', 'rejected', 'duplicate')");

            migrationBuilder.AddCheckConstraint(
                name: "incoming_transactions_version_positive",
                table: "incoming_transactions",
                sql: "version >= 1");

            migrationBuilder.CreateIndex(
                name: "manual_command_receipts_key_unique",
                table: "manual_command_receipts",
                columns: new[] { "tenant_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payment_allocations_payment_request_id_tenant_id_bank_conne",
                table: "payment_allocations",
                columns: new[] { "payment_request_id", "tenant_id", "bank_connection_id" });

            migrationBuilder.CreateIndex(
                name: "ix_payment_allocations_transaction_id_tenant_id_bank_connectio",
                table: "payment_allocations",
                columns: new[] { "transaction_id", "tenant_id", "bank_connection_id" });

            migrationBuilder.CreateIndex(
                name: "payment_allocations_one_active_per_transaction",
                table: "payment_allocations",
                column: "transaction_id",
                unique: true,
                filter: "state = 'active'");

            migrationBuilder.CreateIndex(
                name: "payment_allocations_request_idx",
                table: "payment_allocations",
                columns: new[] { "payment_request_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_payment_request_changes_allocation_id",
                table: "payment_request_changes",
                column: "allocation_id");

            migrationBuilder.CreateIndex(
                name: "ix_payment_request_changes_payment_request_id_tenant_id_bank_c",
                table: "payment_request_changes",
                columns: new[] { "payment_request_id", "tenant_id", "bank_connection_id" });

            migrationBuilder.CreateIndex(
                name: "payment_request_changes_sequence_unique",
                table: "payment_request_changes",
                columns: new[] { "payment_request_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "payment_request_changes_tenant_idx",
                table: "payment_request_changes",
                columns: new[] { "tenant_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_payment_review_case_members_case_id_tenant_id_bank_connecti",
                table: "payment_review_case_members",
                columns: new[] { "case_id", "tenant_id", "bank_connection_id" });

            migrationBuilder.CreateIndex(
                name: "ix_payment_review_case_members_parse_run_id_tenant_id_bank_con",
                table: "payment_review_case_members",
                columns: new[] { "parse_run_id", "tenant_id", "bank_connection_id" });

            migrationBuilder.CreateIndex(
                name: "ix_payment_review_case_members_transaction_id_tenant_id_bank_c",
                table: "payment_review_case_members",
                columns: new[] { "transaction_id", "tenant_id", "bank_connection_id" });

            migrationBuilder.CreateIndex(
                name: "payment_review_case_members_run_unique",
                table: "payment_review_case_members",
                columns: new[] { "case_id", "parse_run_id" },
                unique: true,
                filter: "parse_run_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "payment_review_case_members_transaction_idx",
                table: "payment_review_case_members",
                column: "transaction_id");

            migrationBuilder.CreateIndex(
                name: "payment_review_case_members_transaction_unique",
                table: "payment_review_case_members",
                columns: new[] { "case_id", "transaction_id" },
                unique: true,
                filter: "transaction_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_payment_review_cases_tenant_id_bank_connection_id",
                table: "payment_review_cases",
                columns: new[] { "tenant_id", "bank_connection_id" });

            migrationBuilder.CreateIndex(
                name: "payment_review_cases_message_unique",
                table: "payment_review_cases",
                column: "inbound_message_id",
                unique: true,
                filter: "inbound_message_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "payment_review_cases_tenant_state_idx",
                table: "payment_review_cases",
                columns: new[] { "tenant_id", "state", "id" });

            migrationBuilder.CreateIndex(
                name: "payment_review_decisions_case_idx",
                table: "payment_review_decisions",
                columns: new[] { "case_id", "created_at" });

            migrationBuilder.AddForeignKey(
                name: "fk_incoming_transactions_incoming_transactions_canonical_trans",
                table: "incoming_transactions",
                columns: new[] { "canonical_transaction_id", "tenant_id", "bank_connection_id" },
                principalTable: "incoming_transactions",
                principalColumns: new[] { "id", "tenant_id", "bank_connection_id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_incoming_transactions_incoming_transactions_canonical_trans",
                table: "incoming_transactions");

            migrationBuilder.DropTable(
                name: "manual_command_receipts");

            migrationBuilder.DropTable(
                name: "payment_request_changes");

            migrationBuilder.DropTable(
                name: "payment_review_case_members");

            migrationBuilder.DropTable(
                name: "payment_review_decisions");

            migrationBuilder.DropTable(
                name: "payment_allocations");

            migrationBuilder.DropTable(
                name: "payment_review_cases");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_payment_requests_id_tenant_id_bank_connection_id",
                table: "payment_requests");

            migrationBuilder.DropCheckConstraint(
                name: "payment_requests_change_sequence_valid",
                table: "payment_requests");

            migrationBuilder.DropCheckConstraint(
                name: "payment_requests_paid_consistent",
                table: "payment_requests");

            migrationBuilder.DropCheckConstraint(
                name: "payment_requests_status_valid",
                table: "payment_requests");

            migrationBuilder.DropIndex(
                name: "incoming_transactions_unallocated_symbol_idx",
                table: "incoming_transactions");

            migrationBuilder.DropIndex(
                name: "ix_incoming_transactions_canonical_transaction_id_tenant_id_ba",
                table: "incoming_transactions");

            migrationBuilder.DropCheckConstraint(
                name: "incoming_transactions_canonical_consistent",
                table: "incoming_transactions");

            migrationBuilder.DropCheckConstraint(
                name: "incoming_transactions_handling_valid",
                table: "incoming_transactions");

            migrationBuilder.DropCheckConstraint(
                name: "incoming_transactions_version_positive",
                table: "incoming_transactions");

            migrationBuilder.DropColumn(
                name: "change_sequence",
                table: "payment_requests");

            migrationBuilder.DropColumn(
                name: "paid_amount",
                table: "payment_requests");

            migrationBuilder.DropColumn(
                name: "auto_match_block_reason",
                table: "incoming_transactions");

            migrationBuilder.DropColumn(
                name: "auto_match_blocked",
                table: "incoming_transactions");

            migrationBuilder.DropColumn(
                name: "canonical_transaction_id",
                table: "incoming_transactions");

            migrationBuilder.DropColumn(
                name: "confirmation_decision_id",
                table: "incoming_transactions");

            migrationBuilder.DropColumn(
                name: "handled_at",
                table: "incoming_transactions");

            migrationBuilder.DropColumn(
                name: "handled_by",
                table: "incoming_transactions");

            migrationBuilder.DropColumn(
                name: "handling",
                table: "incoming_transactions");

            migrationBuilder.DropColumn(
                name: "handling_reason",
                table: "incoming_transactions");

            migrationBuilder.DropColumn(
                name: "manually_confirmed",
                table: "incoming_transactions");

            migrationBuilder.DropColumn(
                name: "match_checked_at",
                table: "incoming_transactions");

            migrationBuilder.DropColumn(
                name: "match_request_id",
                table: "incoming_transactions");

            migrationBuilder.DropColumn(
                name: "match_result",
                table: "incoming_transactions");

            migrationBuilder.DropColumn(
                name: "version",
                table: "incoming_transactions");

            migrationBuilder.AddCheckConstraint(
                name: "payment_requests_status_valid",
                table: "payment_requests",
                sql: "status IN ('open', 'cancelled')");
        }
    }
}
