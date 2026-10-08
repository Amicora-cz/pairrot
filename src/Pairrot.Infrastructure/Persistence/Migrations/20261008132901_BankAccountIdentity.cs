using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pairrot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BankAccountIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_incoming_transactions_bank_connections_tenant_id_bank_conne",
                table: "incoming_transactions");

            migrationBuilder.DropForeignKey(
                name: "fk_incoming_transactions_incoming_transactions_canonical_trans",
                table: "incoming_transactions");

            migrationBuilder.DropForeignKey(
                name: "fk_message_parse_runs_bank_connections_tenant_id_bank_connecti",
                table: "message_parse_runs");

            migrationBuilder.DropForeignKey(
                name: "fk_message_parse_runs_incoming_transactions_transaction_id_ten",
                table: "message_parse_runs");

            migrationBuilder.DropForeignKey(
                name: "fk_payment_allocations_payment_requests_payment_request_id_ten",
                table: "payment_allocations");

            migrationBuilder.DropForeignKey(
                name: "fk_payment_requests_bank_connections_tenant_id_bank_connection",
                table: "payment_requests");

            migrationBuilder.DropForeignKey(
                name: "fk_payment_review_case_members_incoming_transactions_transacti",
                table: "payment_review_case_members");

            migrationBuilder.DropForeignKey(
                name: "fk_payment_review_case_members_message_parse_runs_parse_run_id",
                table: "payment_review_case_members");

            migrationBuilder.DropForeignKey(
                name: "fk_payment_review_case_members_payment_review_cases_case_id_te",
                table: "payment_review_case_members");

            migrationBuilder.DropForeignKey(
                name: "fk_payment_review_cases_bank_connections_tenant_id_bank_connec",
                table: "payment_review_cases");

            migrationBuilder.DropForeignKey(
                name: "fk_transaction_sources_incoming_transactions_transaction_id_te",
                table: "transaction_sources");

            migrationBuilder.DropForeignKey(
                name: "fk_variable_symbol_allocators_bank_connections_bank_connection",
                table: "variable_symbol_allocators");

            migrationBuilder.DropIndex(
                name: "ix_transaction_sources_transaction_id_tenant_id_bank_connectio",
                table: "transaction_sources");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_payment_review_cases_id_tenant_id_bank_connection_id",
                table: "payment_review_cases");

            migrationBuilder.DropIndex(
                name: "ix_payment_review_cases_tenant_id_bank_connection_id",
                table: "payment_review_cases");

            migrationBuilder.DropCheckConstraint(
                name: "payment_review_cases_issue_valid",
                table: "payment_review_cases");

            migrationBuilder.DropIndex(
                name: "ix_payment_review_case_members_case_id_tenant_id_bank_connecti",
                table: "payment_review_case_members");

            migrationBuilder.DropIndex(
                name: "ix_payment_review_case_members_parse_run_id_tenant_id_bank_con",
                table: "payment_review_case_members");

            migrationBuilder.DropIndex(
                name: "ix_payment_review_case_members_transaction_id_tenant_id_bank_c",
                table: "payment_review_case_members");

            migrationBuilder.DropIndex(
                name: "ix_payment_requests_tenant_id_bank_connection_id",
                table: "payment_requests");

            migrationBuilder.DropIndex(
                name: "payment_requests_connection_variable_symbol_number_idx",
                table: "payment_requests");

            migrationBuilder.DropIndex(
                name: "payment_requests_connection_variable_symbol_unique",
                table: "payment_requests");

            migrationBuilder.DropIndex(
                name: "ix_payment_allocations_payment_request_id_tenant_id_bank_conne",
                table: "payment_allocations");

            migrationBuilder.DropIndex(
                name: "ix_message_parse_runs_tenant_id_bank_connection_id",
                table: "message_parse_runs");

            migrationBuilder.DropIndex(
                name: "ix_message_parse_runs_transaction_id_tenant_id_bank_connection",
                table: "message_parse_runs");

            migrationBuilder.DropIndex(
                name: "incoming_transactions_bank_reference_unique",
                table: "incoming_transactions");

            migrationBuilder.DropIndex(
                name: "incoming_transactions_fingerprint_idx",
                table: "incoming_transactions");

            migrationBuilder.DropIndex(
                name: "incoming_transactions_unallocated_symbol_idx",
                table: "incoming_transactions");

            migrationBuilder.DropIndex(
                name: "ix_incoming_transactions_canonical_transaction_id_tenant_id_ba",
                table: "incoming_transactions");

            migrationBuilder.DropIndex(
                name: "ix_incoming_transactions_tenant_id_bank_connection_id",
                table: "incoming_transactions");

            migrationBuilder.DropIndex(
                name: "bank_connections_iban_unique",
                table: "bank_connections");

            migrationBuilder.DropCheckConstraint(
                name: "bank_connections_archive_consistent",
                table: "bank_connections");

            // Feature 013 data migration. Runs in the migration's transaction; the self-check at the
            // end raises an error, and so rolls everything back, if any invariant does not hold.

            migrationBuilder.CreateTable(
                name: "bank_accounts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    iban = table.Column<string>(type: "character varying(34)", maxLength: 34, nullable: false),
                    account_number = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    bank_code = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bank_accounts", x => x.id);
                    table.CheckConstraint("bank_accounts_account_number_not_blank", "btrim(account_number) <> ''");
                    table.CheckConstraint("bank_accounts_bank_code_format", "bank_code ~ '^[0-9]{4}$'");
                    table.CheckConstraint("bank_accounts_currency_valid", "currency = 'CZK'");
                    table.CheckConstraint("bank_accounts_iban_format", "iban ~ '^CZ[0-9]{22}$'");
                });

            migrationBuilder.Sql("""
                -- 1. One account per distinct normalized IBAN; every connection references it.
                INSERT INTO bank_accounts (id, iban, account_number, bank_code, currency, created_at)
                SELECT gen_random_uuid(), b.iban, min(b.account_number), substr(b.iban, 5, 4), 'CZK', min(b.created_at)
                FROM bank_connections b
                GROUP BY b.iban
                ON CONFLICT DO NOTHING;

                ALTER TABLE bank_connections ADD COLUMN bank_account_id uuid;
                UPDATE bank_connections b SET bank_account_id = a.id FROM bank_accounts a WHERE a.iban = b.iban;
                ALTER TABLE bank_connections ALTER COLUMN bank_account_id SET NOT NULL;

                -- 2. Allocators move to the account (one-to-one today: IBANs were unique per connection).
                ALTER TABLE variable_symbol_allocators RENAME COLUMN bank_connection_id TO bank_account_id;
                UPDATE variable_symbol_allocators v SET bank_account_id = b.bank_account_id
                FROM bank_connections b WHERE b.id = v.bank_account_id;

                -- 3. Every record of a connection states its account.
                ALTER TABLE payment_requests ADD COLUMN bank_account_id uuid;
                UPDATE payment_requests p SET bank_account_id = b.bank_account_id FROM bank_connections b WHERE b.id = p.bank_connection_id;
                ALTER TABLE payment_requests ALTER COLUMN bank_account_id SET NOT NULL;

                ALTER TABLE payment_request_changes ADD COLUMN bank_account_id uuid;
                UPDATE payment_request_changes c SET bank_account_id = b.bank_account_id FROM bank_connections b WHERE b.id = c.bank_connection_id;
                ALTER TABLE payment_request_changes ALTER COLUMN bank_account_id SET NOT NULL;

                ALTER TABLE incoming_transactions ADD COLUMN bank_account_id uuid;
                UPDATE incoming_transactions t SET bank_account_id = b.bank_account_id FROM bank_connections b WHERE b.id = t.bank_connection_id;
                ALTER TABLE incoming_transactions ALTER COLUMN bank_account_id SET NOT NULL;

                ALTER TABLE message_parse_runs ADD COLUMN bank_account_id uuid;
                UPDATE message_parse_runs r SET bank_account_id = b.bank_account_id FROM bank_connections b WHERE b.id = r.bank_connection_id;
                ALTER TABLE message_parse_runs ALTER COLUMN bank_account_id SET NOT NULL;

                ALTER TABLE transaction_sources ADD COLUMN bank_account_id uuid;
                UPDATE transaction_sources s SET bank_account_id = b.bank_account_id FROM bank_connections b WHERE b.id = s.bank_connection_id;
                ALTER TABLE transaction_sources ALTER COLUMN bank_account_id SET NOT NULL;

                ALTER TABLE payment_review_cases ADD COLUMN bank_account_id uuid;
                UPDATE payment_review_cases c SET bank_account_id = b.bank_account_id FROM bank_connections b WHERE b.id = c.bank_connection_id;
                ALTER TABLE payment_review_cases ALTER COLUMN bank_account_id SET NOT NULL;

                ALTER TABLE payment_review_case_members ADD COLUMN bank_account_id uuid;
                UPDATE payment_review_case_members m SET bank_account_id = b.bank_account_id FROM bank_connections b WHERE b.id = m.bank_connection_id;
                ALTER TABLE payment_review_case_members ALTER COLUMN bank_account_id SET NOT NULL;

                -- Allocations keep both connection IDs; before this feature both sides had the same one.
                ALTER TABLE payment_allocations ADD COLUMN bank_account_id uuid;
                ALTER TABLE payment_allocations ADD COLUMN payment_request_bank_connection_id uuid;
                UPDATE payment_allocations a SET bank_account_id = b.bank_account_id, payment_request_bank_connection_id = a.bank_connection_id
                FROM bank_connections b WHERE b.id = a.bank_connection_id;
                ALTER TABLE payment_allocations ALTER COLUMN bank_account_id SET NOT NULL;
                ALTER TABLE payment_allocations ALTER COLUMN payment_request_bank_connection_id SET NOT NULL;

                ALTER TABLE incoming_transactions ADD COLUMN movement_conflict boolean NOT NULL DEFAULT false;
                ALTER TABLE payment_review_decisions ADD COLUMN reason_code character varying(50);

                -- Comparison fingerprints v2: the account replaces the connection in the canonical
                -- JSON tuple of BankMessageEvaluator.ComparisonFingerprint (no spaces, null for absent values).
                UPDATE incoming_transactions SET comparison_fingerprint = encode(sha256(convert_to(
                    '["v2",' || to_json(bank_profile)::text || ',' || to_json(bank_account_id::text)::text || ','
                    || amount::text || ',' || to_json(currency)::text || ','
                    || to_json(to_char(booked_on, 'YYYY-MM-DD'))::text || ','
                    || coalesce(to_json(variable_symbol_key)::text, 'null') || ']', 'UTF8')), 'hex');

                -- 4. Archived reasons; deleted tenants release their accounts.
                ALTER TABLE bank_connections ADD COLUMN archived_reason character varying(20);
                UPDATE bank_connections SET archived_reason = 'user' WHERE status = 'archived';
                UPDATE bank_connections b SET status = 'archived', archived_at = coalesce(t.deleted_at, now()), archived_reason = 'tenant_deleted',
                    updated_at = now()
                FROM tenants t
                WHERE t.id = b.tenant_id AND t.status = 'deleted' AND b.status = 'active';
                """);

            migrationBuilder.AddUniqueConstraint(
                name: "ak_payment_review_cases_id_tenant_id_bank_connection_id_bank_a",
                table: "payment_review_cases",
                columns: new[] { "id", "tenant_id", "bank_connection_id", "bank_account_id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_message_parse_runs_id_tenant_id_bank_account_id",
                table: "message_parse_runs",
                columns: new[] { "id", "tenant_id", "bank_account_id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_incoming_transactions_id_tenant_id_bank_account_id",
                table: "incoming_transactions",
                columns: new[] { "id", "tenant_id", "bank_account_id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_bank_connections_tenant_id_id_bank_account_id",
                table: "bank_connections",
                columns: new[] { "tenant_id", "id", "bank_account_id" });

            migrationBuilder.CreateIndex(
                name: "ix_transaction_sources_tenant_id_bank_connection_id_bank_accou",
                table: "transaction_sources",
                columns: new[] { "tenant_id", "bank_connection_id", "bank_account_id" });

            migrationBuilder.CreateIndex(
                name: "ix_transaction_sources_transaction_id_tenant_id_bank_account_id",
                table: "transaction_sources",
                columns: new[] { "transaction_id", "tenant_id", "bank_account_id" });

            migrationBuilder.CreateIndex(
                name: "ix_payment_review_cases_tenant_id_bank_connection_id_bank_acco",
                table: "payment_review_cases",
                columns: new[] { "tenant_id", "bank_connection_id", "bank_account_id" });

            migrationBuilder.AddCheckConstraint(
                name: "payment_review_cases_issue_valid",
                table: "payment_review_cases",
                sql: "issue IN ('unverified_candidate', 'possible_duplicate', 'disputed', 'account_movement_conflict')");

            migrationBuilder.CreateIndex(
                name: "ix_payment_review_case_members_case_id_tenant_id_bank_connecti",
                table: "payment_review_case_members",
                columns: new[] { "case_id", "tenant_id", "bank_connection_id", "bank_account_id" });

            migrationBuilder.CreateIndex(
                name: "ix_payment_review_case_members_parse_run_id_tenant_id_bank_acc",
                table: "payment_review_case_members",
                columns: new[] { "parse_run_id", "tenant_id", "bank_account_id" });

            migrationBuilder.CreateIndex(
                name: "ix_payment_review_case_members_transaction_id_tenant_id_bank_a",
                table: "payment_review_case_members",
                columns: new[] { "transaction_id", "tenant_id", "bank_account_id" });

            migrationBuilder.CreateIndex(
                name: "ix_payment_requests_tenant_id_bank_connection_id_bank_account_",
                table: "payment_requests",
                columns: new[] { "tenant_id", "bank_connection_id", "bank_account_id" });

            migrationBuilder.CreateIndex(
                name: "payment_requests_account_variable_symbol_number_idx",
                table: "payment_requests",
                columns: new[] { "bank_account_id", "variable_symbol_number" });

            migrationBuilder.CreateIndex(
                name: "payment_requests_account_variable_symbol_unique",
                table: "payment_requests",
                columns: new[] { "bank_account_id", "variable_symbol_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "payment_requests_connection_idx",
                table: "payment_requests",
                columns: new[] { "bank_connection_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_payment_request_changes_tenant_id_bank_connection_id_bank_a",
                table: "payment_request_changes",
                columns: new[] { "tenant_id", "bank_connection_id", "bank_account_id" });

            migrationBuilder.CreateIndex(
                name: "ix_payment_allocations_payment_request_id_tenant_id_payment_re",
                table: "payment_allocations",
                columns: new[] { "payment_request_id", "tenant_id", "payment_request_bank_connection_id" });

            migrationBuilder.CreateIndex(
                name: "ix_payment_allocations_tenant_id_bank_connection_id_bank_accou",
                table: "payment_allocations",
                columns: new[] { "tenant_id", "bank_connection_id", "bank_account_id" });

            migrationBuilder.CreateIndex(
                name: "ix_payment_allocations_tenant_id_payment_request_bank_connecti",
                table: "payment_allocations",
                columns: new[] { "tenant_id", "payment_request_bank_connection_id", "bank_account_id" });

            migrationBuilder.CreateIndex(
                name: "ix_message_parse_runs_tenant_id_bank_connection_id_bank_accoun",
                table: "message_parse_runs",
                columns: new[] { "tenant_id", "bank_connection_id", "bank_account_id" });

            migrationBuilder.CreateIndex(
                name: "ix_message_parse_runs_transaction_id_tenant_id_bank_account_id",
                table: "message_parse_runs",
                columns: new[] { "transaction_id", "tenant_id", "bank_account_id" });

            migrationBuilder.CreateIndex(
                name: "incoming_transactions_account_bank_reference_unique",
                table: "incoming_transactions",
                columns: new[] { "bank_account_id", "bank_profile", "bank_transaction_id" },
                unique: true,
                filter: "bank_transaction_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "incoming_transactions_account_fingerprint_idx",
                table: "incoming_transactions",
                columns: new[] { "bank_account_id", "comparison_fingerprint" });

            migrationBuilder.CreateIndex(
                name: "incoming_transactions_connection_idx",
                table: "incoming_transactions",
                columns: new[] { "bank_connection_id", "id" });

            migrationBuilder.CreateIndex(
                name: "incoming_transactions_unallocated_symbol_idx",
                table: "incoming_transactions",
                columns: new[] { "bank_account_id", "variable_symbol_key" },
                filter: "handling = 'unallocated'");

            migrationBuilder.CreateIndex(
                name: "ix_incoming_transactions_canonical_transaction_id_tenant_id_ba",
                table: "incoming_transactions",
                columns: new[] { "canonical_transaction_id", "tenant_id", "bank_account_id" });

            migrationBuilder.CreateIndex(
                name: "ix_incoming_transactions_tenant_id_bank_connection_id_bank_acc",
                table: "incoming_transactions",
                columns: new[] { "tenant_id", "bank_connection_id", "bank_account_id" });

            migrationBuilder.CreateIndex(
                name: "bank_connections_iban_idx",
                table: "bank_connections",
                column: "iban");

            migrationBuilder.CreateIndex(
                name: "bank_connections_one_active_per_account",
                table: "bank_connections",
                column: "bank_account_id",
                unique: true,
                filter: "status = 'active'");

            migrationBuilder.AddCheckConstraint(
                name: "bank_connections_archive_consistent",
                table: "bank_connections",
                sql: "(status = 'archived' AND archived_at IS NOT NULL AND archived_reason IS NOT NULL) OR (status <> 'archived' AND archived_at IS NULL AND archived_reason IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "bank_connections_archived_reason_valid",
                table: "bank_connections",
                sql: "archived_reason IS NULL OR archived_reason IN ('user', 'tenant_deleted', 'operator')");

            migrationBuilder.CreateIndex(
                name: "bank_accounts_iban_unique",
                table: "bank_accounts",
                column: "iban",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_bank_connections_bank_accounts_bank_account_id",
                table: "bank_connections",
                column: "bank_account_id",
                principalTable: "bank_accounts",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_incoming_transactions_bank_connections_tenant_id_bank_conne",
                table: "incoming_transactions",
                columns: new[] { "tenant_id", "bank_connection_id", "bank_account_id" },
                principalTable: "bank_connections",
                principalColumns: new[] { "tenant_id", "id", "bank_account_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_incoming_transactions_incoming_transactions_canonical_trans",
                table: "incoming_transactions",
                columns: new[] { "canonical_transaction_id", "tenant_id", "bank_account_id" },
                principalTable: "incoming_transactions",
                principalColumns: new[] { "id", "tenant_id", "bank_account_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_message_parse_runs_bank_connections_tenant_id_bank_connecti",
                table: "message_parse_runs",
                columns: new[] { "tenant_id", "bank_connection_id", "bank_account_id" },
                principalTable: "bank_connections",
                principalColumns: new[] { "tenant_id", "id", "bank_account_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_message_parse_runs_incoming_transactions_transaction_id_ten",
                table: "message_parse_runs",
                columns: new[] { "transaction_id", "tenant_id", "bank_account_id" },
                principalTable: "incoming_transactions",
                principalColumns: new[] { "id", "tenant_id", "bank_account_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_payment_allocations_bank_connections_tenant_id_bank_connect",
                table: "payment_allocations",
                columns: new[] { "tenant_id", "bank_connection_id", "bank_account_id" },
                principalTable: "bank_connections",
                principalColumns: new[] { "tenant_id", "id", "bank_account_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_payment_allocations_bank_connections_tenant_id_payment_requ",
                table: "payment_allocations",
                columns: new[] { "tenant_id", "payment_request_bank_connection_id", "bank_account_id" },
                principalTable: "bank_connections",
                principalColumns: new[] { "tenant_id", "id", "bank_account_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_payment_allocations_payment_requests_payment_request_id_ten",
                table: "payment_allocations",
                columns: new[] { "payment_request_id", "tenant_id", "payment_request_bank_connection_id" },
                principalTable: "payment_requests",
                principalColumns: new[] { "id", "tenant_id", "bank_connection_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_payment_request_changes_bank_connections_tenant_id_bank_con",
                table: "payment_request_changes",
                columns: new[] { "tenant_id", "bank_connection_id", "bank_account_id" },
                principalTable: "bank_connections",
                principalColumns: new[] { "tenant_id", "id", "bank_account_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_payment_requests_bank_connections_tenant_id_bank_connection",
                table: "payment_requests",
                columns: new[] { "tenant_id", "bank_connection_id", "bank_account_id" },
                principalTable: "bank_connections",
                principalColumns: new[] { "tenant_id", "id", "bank_account_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_payment_review_case_members_incoming_transactions_transacti",
                table: "payment_review_case_members",
                columns: new[] { "transaction_id", "tenant_id", "bank_account_id" },
                principalTable: "incoming_transactions",
                principalColumns: new[] { "id", "tenant_id", "bank_account_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_payment_review_case_members_message_parse_runs_parse_run_id",
                table: "payment_review_case_members",
                columns: new[] { "parse_run_id", "tenant_id", "bank_account_id" },
                principalTable: "message_parse_runs",
                principalColumns: new[] { "id", "tenant_id", "bank_account_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_payment_review_case_members_payment_review_cases_case_id_te",
                table: "payment_review_case_members",
                columns: new[] { "case_id", "tenant_id", "bank_connection_id", "bank_account_id" },
                principalTable: "payment_review_cases",
                principalColumns: new[] { "id", "tenant_id", "bank_connection_id", "bank_account_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_payment_review_cases_bank_connections_tenant_id_bank_connec",
                table: "payment_review_cases",
                columns: new[] { "tenant_id", "bank_connection_id", "bank_account_id" },
                principalTable: "bank_connections",
                principalColumns: new[] { "tenant_id", "id", "bank_account_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_transaction_sources_bank_connections_tenant_id_bank_connect",
                table: "transaction_sources",
                columns: new[] { "tenant_id", "bank_connection_id", "bank_account_id" },
                principalTable: "bank_connections",
                principalColumns: new[] { "tenant_id", "id", "bank_account_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_transaction_sources_incoming_transactions_transaction_id_te",
                table: "transaction_sources",
                columns: new[] { "transaction_id", "tenant_id", "bank_account_id" },
                principalTable: "incoming_transactions",
                principalColumns: new[] { "id", "tenant_id", "bank_account_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_variable_symbol_allocators_bank_accounts_bank_account_id",
                table: "variable_symbol_allocators",
                column: "bank_account_id",
                principalTable: "bank_accounts",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            // 7. Self-check; any failure aborts the migration and rolls everything back.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    problems text[] := ARRAY[]::text[];
                BEGIN
                    IF EXISTS (SELECT 1 FROM bank_connections b LEFT JOIN bank_accounts a ON a.id = b.bank_account_id
                               WHERE a.id IS NULL OR a.iban <> b.iban) THEN
                        problems := array_append(problems, 'a bank connection has no matching account');
                    END IF;
                    IF (SELECT count(*) FROM bank_accounts) <> (SELECT count(DISTINCT iban) FROM bank_connections) THEN
                        problems := array_append(problems, 'the number of accounts differs from the number of distinct IBANs');
                    END IF;
                    IF EXISTS (SELECT 1 FROM variable_symbol_allocators v
                               WHERE NOT EXISTS (SELECT 1 FROM bank_connections b WHERE b.bank_account_id = v.bank_account_id)) THEN
                        problems := array_append(problems, 'an allocator has no account');
                    END IF;
                    IF EXISTS (SELECT 1 FROM bank_connections WHERE status = 'active' GROUP BY bank_account_id HAVING count(*) > 1) THEN
                        problems := array_append(problems, 'an account has two active registrations');
                    END IF;
                    IF EXISTS (SELECT 1 FROM incoming_transactions t JOIN bank_connections b ON b.id = t.bank_connection_id
                               WHERE b.bank_account_id <> t.bank_account_id OR b.tenant_id <> t.tenant_id) THEN
                        problems := array_append(problems, 'a transaction''s account differs from its connection''s');
                    END IF;
                    IF EXISTS (SELECT 1 FROM incoming_transactions WHERE bank_transaction_id IS NOT NULL
                               GROUP BY bank_account_id, bank_profile, bank_transaction_id HAVING count(*) > 1) THEN
                        problems := array_append(problems, 'a bank reference is recorded twice on one account');
                    END IF;
                    IF EXISTS (SELECT 1 FROM incoming_transactions WHERE comparison_fingerprint <> encode(sha256(convert_to(
                        '["v2",' || to_json(bank_profile)::text || ',' || to_json(bank_account_id::text)::text || ','
                        || amount::text || ',' || to_json(currency)::text || ','
                        || to_json(to_char(booked_on, 'YYYY-MM-DD'))::text || ','
                        || coalesce(to_json(variable_symbol_key)::text, 'null') || ']', 'UTF8')), 'hex')) THEN
                        problems := array_append(problems, 'a comparison fingerprint does not recompute as v2');
                    END IF;
                    IF EXISTS (SELECT 1 FROM payment_allocations a
                               JOIN incoming_transactions t ON t.id = a.transaction_id
                               JOIN payment_requests p ON p.id = a.payment_request_id
                               WHERE t.tenant_id <> a.tenant_id OR p.tenant_id <> a.tenant_id
                                  OR t.bank_account_id <> a.bank_account_id OR p.bank_account_id <> a.bank_account_id) THEN
                        problems := array_append(problems, 'an allocation joins different owners or accounts');
                    END IF;
                    -- payments check: paid amounts equal active allocations, and only eligible receipts are counted.
                    IF EXISTS (SELECT 1 FROM payment_requests p
                               WHERE p.paid_amount <> coalesce((SELECT sum(a.amount) FROM payment_allocations a
                                                                WHERE a.payment_request_id = p.id AND a.state = 'active'), 0)) THEN
                        problems := array_append(problems, 'a paid amount differs from its active allocations');
                    END IF;
                    IF EXISTS (SELECT 1 FROM payment_allocations a JOIN incoming_transactions t ON t.id = a.transaction_id
                               WHERE a.state = 'active' AND (t.handling <> 'allocated'
                                   OR (t.ingestion_status <> 'accepted' AND NOT t.manually_confirmed)
                                   OR EXISTS (SELECT 1 FROM payment_review_case_members m JOIN payment_review_cases c ON c.id = m.case_id
                                              WHERE m.transaction_id = t.id AND c.state = 'open'))) THEN
                        problems := array_append(problems, 'an active allocation counts an ineligible transaction');
                    END IF;
                    IF array_length(problems, 1) > 0 THEN
                        RAISE EXCEPTION 'Feature 013 migration self-check failed: %', array_to_string(problems, '; ');
                    END IF;
                END
                $$;
                """);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_bank_connections_bank_accounts_bank_account_id",
                table: "bank_connections");

            migrationBuilder.DropForeignKey(
                name: "fk_incoming_transactions_bank_connections_tenant_id_bank_conne",
                table: "incoming_transactions");

            migrationBuilder.DropForeignKey(
                name: "fk_incoming_transactions_incoming_transactions_canonical_trans",
                table: "incoming_transactions");

            migrationBuilder.DropForeignKey(
                name: "fk_message_parse_runs_bank_connections_tenant_id_bank_connecti",
                table: "message_parse_runs");

            migrationBuilder.DropForeignKey(
                name: "fk_message_parse_runs_incoming_transactions_transaction_id_ten",
                table: "message_parse_runs");

            migrationBuilder.DropForeignKey(
                name: "fk_payment_allocations_bank_connections_tenant_id_bank_connect",
                table: "payment_allocations");

            migrationBuilder.DropForeignKey(
                name: "fk_payment_allocations_bank_connections_tenant_id_payment_requ",
                table: "payment_allocations");

            migrationBuilder.DropForeignKey(
                name: "fk_payment_allocations_payment_requests_payment_request_id_ten",
                table: "payment_allocations");

            migrationBuilder.DropForeignKey(
                name: "fk_payment_request_changes_bank_connections_tenant_id_bank_con",
                table: "payment_request_changes");

            migrationBuilder.DropForeignKey(
                name: "fk_payment_requests_bank_connections_tenant_id_bank_connection",
                table: "payment_requests");

            migrationBuilder.DropForeignKey(
                name: "fk_payment_review_case_members_incoming_transactions_transacti",
                table: "payment_review_case_members");

            migrationBuilder.DropForeignKey(
                name: "fk_payment_review_case_members_message_parse_runs_parse_run_id",
                table: "payment_review_case_members");

            migrationBuilder.DropForeignKey(
                name: "fk_payment_review_case_members_payment_review_cases_case_id_te",
                table: "payment_review_case_members");

            migrationBuilder.DropForeignKey(
                name: "fk_payment_review_cases_bank_connections_tenant_id_bank_connec",
                table: "payment_review_cases");

            migrationBuilder.DropForeignKey(
                name: "fk_transaction_sources_bank_connections_tenant_id_bank_connect",
                table: "transaction_sources");

            migrationBuilder.DropForeignKey(
                name: "fk_transaction_sources_incoming_transactions_transaction_id_te",
                table: "transaction_sources");

            migrationBuilder.DropForeignKey(
                name: "fk_variable_symbol_allocators_bank_accounts_bank_account_id",
                table: "variable_symbol_allocators");

            migrationBuilder.DropTable(
                name: "bank_accounts");

            migrationBuilder.DropIndex(
                name: "ix_transaction_sources_tenant_id_bank_connection_id_bank_accou",
                table: "transaction_sources");

            migrationBuilder.DropIndex(
                name: "ix_transaction_sources_transaction_id_tenant_id_bank_account_id",
                table: "transaction_sources");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_payment_review_cases_id_tenant_id_bank_connection_id_bank_a",
                table: "payment_review_cases");

            migrationBuilder.DropIndex(
                name: "ix_payment_review_cases_tenant_id_bank_connection_id_bank_acco",
                table: "payment_review_cases");

            migrationBuilder.DropCheckConstraint(
                name: "payment_review_cases_issue_valid",
                table: "payment_review_cases");

            migrationBuilder.DropIndex(
                name: "ix_payment_review_case_members_case_id_tenant_id_bank_connecti",
                table: "payment_review_case_members");

            migrationBuilder.DropIndex(
                name: "ix_payment_review_case_members_parse_run_id_tenant_id_bank_acc",
                table: "payment_review_case_members");

            migrationBuilder.DropIndex(
                name: "ix_payment_review_case_members_transaction_id_tenant_id_bank_a",
                table: "payment_review_case_members");

            migrationBuilder.DropIndex(
                name: "ix_payment_requests_tenant_id_bank_connection_id_bank_account_",
                table: "payment_requests");

            migrationBuilder.DropIndex(
                name: "payment_requests_account_variable_symbol_number_idx",
                table: "payment_requests");

            migrationBuilder.DropIndex(
                name: "payment_requests_account_variable_symbol_unique",
                table: "payment_requests");

            migrationBuilder.DropIndex(
                name: "payment_requests_connection_idx",
                table: "payment_requests");

            migrationBuilder.DropIndex(
                name: "ix_payment_request_changes_tenant_id_bank_connection_id_bank_a",
                table: "payment_request_changes");

            migrationBuilder.DropIndex(
                name: "ix_payment_allocations_payment_request_id_tenant_id_payment_re",
                table: "payment_allocations");

            migrationBuilder.DropIndex(
                name: "ix_payment_allocations_tenant_id_bank_connection_id_bank_accou",
                table: "payment_allocations");

            migrationBuilder.DropIndex(
                name: "ix_payment_allocations_tenant_id_payment_request_bank_connecti",
                table: "payment_allocations");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_message_parse_runs_id_tenant_id_bank_account_id",
                table: "message_parse_runs");

            migrationBuilder.DropIndex(
                name: "ix_message_parse_runs_tenant_id_bank_connection_id_bank_accoun",
                table: "message_parse_runs");

            migrationBuilder.DropIndex(
                name: "ix_message_parse_runs_transaction_id_tenant_id_bank_account_id",
                table: "message_parse_runs");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_incoming_transactions_id_tenant_id_bank_account_id",
                table: "incoming_transactions");

            migrationBuilder.DropIndex(
                name: "incoming_transactions_account_bank_reference_unique",
                table: "incoming_transactions");

            migrationBuilder.DropIndex(
                name: "incoming_transactions_account_fingerprint_idx",
                table: "incoming_transactions");

            migrationBuilder.DropIndex(
                name: "incoming_transactions_connection_idx",
                table: "incoming_transactions");

            migrationBuilder.DropIndex(
                name: "incoming_transactions_unallocated_symbol_idx",
                table: "incoming_transactions");

            migrationBuilder.DropIndex(
                name: "ix_incoming_transactions_canonical_transaction_id_tenant_id_ba",
                table: "incoming_transactions");

            migrationBuilder.DropIndex(
                name: "ix_incoming_transactions_tenant_id_bank_connection_id_bank_acc",
                table: "incoming_transactions");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_bank_connections_tenant_id_id_bank_account_id",
                table: "bank_connections");

            migrationBuilder.DropIndex(
                name: "bank_connections_iban_idx",
                table: "bank_connections");

            migrationBuilder.DropIndex(
                name: "bank_connections_one_active_per_account",
                table: "bank_connections");

            migrationBuilder.DropCheckConstraint(
                name: "bank_connections_archive_consistent",
                table: "bank_connections");

            migrationBuilder.DropCheckConstraint(
                name: "bank_connections_archived_reason_valid",
                table: "bank_connections");

            migrationBuilder.DropColumn(
                name: "bank_account_id",
                table: "transaction_sources");

            migrationBuilder.DropColumn(
                name: "reason_code",
                table: "payment_review_decisions");

            migrationBuilder.DropColumn(
                name: "bank_account_id",
                table: "payment_review_cases");

            migrationBuilder.DropColumn(
                name: "bank_account_id",
                table: "payment_review_case_members");

            migrationBuilder.DropColumn(
                name: "bank_account_id",
                table: "payment_requests");

            migrationBuilder.DropColumn(
                name: "bank_account_id",
                table: "payment_request_changes");

            migrationBuilder.DropColumn(
                name: "bank_account_id",
                table: "payment_allocations");

            migrationBuilder.DropColumn(
                name: "payment_request_bank_connection_id",
                table: "payment_allocations");

            migrationBuilder.DropColumn(
                name: "bank_account_id",
                table: "message_parse_runs");

            migrationBuilder.DropColumn(
                name: "bank_account_id",
                table: "incoming_transactions");

            migrationBuilder.DropColumn(
                name: "movement_conflict",
                table: "incoming_transactions");

            migrationBuilder.DropColumn(
                name: "archived_reason",
                table: "bank_connections");

            migrationBuilder.DropColumn(
                name: "bank_account_id",
                table: "bank_connections");

            migrationBuilder.RenameColumn(
                name: "bank_account_id",
                table: "variable_symbol_allocators",
                newName: "bank_connection_id");

            migrationBuilder.AddUniqueConstraint(
                name: "ak_payment_review_cases_id_tenant_id_bank_connection_id",
                table: "payment_review_cases",
                columns: new[] { "id", "tenant_id", "bank_connection_id" });

            migrationBuilder.CreateIndex(
                name: "ix_transaction_sources_transaction_id_tenant_id_bank_connectio",
                table: "transaction_sources",
                columns: new[] { "transaction_id", "tenant_id", "bank_connection_id" });

            migrationBuilder.CreateIndex(
                name: "ix_payment_review_cases_tenant_id_bank_connection_id",
                table: "payment_review_cases",
                columns: new[] { "tenant_id", "bank_connection_id" });

            migrationBuilder.AddCheckConstraint(
                name: "payment_review_cases_issue_valid",
                table: "payment_review_cases",
                sql: "issue IN ('unverified_candidate', 'possible_duplicate', 'disputed')");

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
                name: "ix_payment_requests_tenant_id_bank_connection_id",
                table: "payment_requests",
                columns: new[] { "tenant_id", "bank_connection_id" });

            migrationBuilder.CreateIndex(
                name: "payment_requests_connection_variable_symbol_number_idx",
                table: "payment_requests",
                columns: new[] { "bank_connection_id", "variable_symbol_number" });

            migrationBuilder.CreateIndex(
                name: "payment_requests_connection_variable_symbol_unique",
                table: "payment_requests",
                columns: new[] { "bank_connection_id", "variable_symbol_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payment_allocations_payment_request_id_tenant_id_bank_conne",
                table: "payment_allocations",
                columns: new[] { "payment_request_id", "tenant_id", "bank_connection_id" });

            migrationBuilder.CreateIndex(
                name: "ix_message_parse_runs_tenant_id_bank_connection_id",
                table: "message_parse_runs",
                columns: new[] { "tenant_id", "bank_connection_id" });

            migrationBuilder.CreateIndex(
                name: "ix_message_parse_runs_transaction_id_tenant_id_bank_connection",
                table: "message_parse_runs",
                columns: new[] { "transaction_id", "tenant_id", "bank_connection_id" });

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
                name: "incoming_transactions_unallocated_symbol_idx",
                table: "incoming_transactions",
                columns: new[] { "bank_connection_id", "variable_symbol_key" },
                filter: "handling = 'unallocated'");

            migrationBuilder.CreateIndex(
                name: "ix_incoming_transactions_canonical_transaction_id_tenant_id_ba",
                table: "incoming_transactions",
                columns: new[] { "canonical_transaction_id", "tenant_id", "bank_connection_id" });

            migrationBuilder.CreateIndex(
                name: "ix_incoming_transactions_tenant_id_bank_connection_id",
                table: "incoming_transactions",
                columns: new[] { "tenant_id", "bank_connection_id" });

            migrationBuilder.CreateIndex(
                name: "bank_connections_iban_unique",
                table: "bank_connections",
                column: "iban",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "bank_connections_archive_consistent",
                table: "bank_connections",
                sql: "(status = 'archived' AND archived_at IS NOT NULL) OR (status <> 'archived' AND archived_at IS NULL)");

            migrationBuilder.AddForeignKey(
                name: "fk_incoming_transactions_bank_connections_tenant_id_bank_conne",
                table: "incoming_transactions",
                columns: new[] { "tenant_id", "bank_connection_id" },
                principalTable: "bank_connections",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_incoming_transactions_incoming_transactions_canonical_trans",
                table: "incoming_transactions",
                columns: new[] { "canonical_transaction_id", "tenant_id", "bank_connection_id" },
                principalTable: "incoming_transactions",
                principalColumns: new[] { "id", "tenant_id", "bank_connection_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_message_parse_runs_bank_connections_tenant_id_bank_connecti",
                table: "message_parse_runs",
                columns: new[] { "tenant_id", "bank_connection_id" },
                principalTable: "bank_connections",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_message_parse_runs_incoming_transactions_transaction_id_ten",
                table: "message_parse_runs",
                columns: new[] { "transaction_id", "tenant_id", "bank_connection_id" },
                principalTable: "incoming_transactions",
                principalColumns: new[] { "id", "tenant_id", "bank_connection_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_payment_allocations_payment_requests_payment_request_id_ten",
                table: "payment_allocations",
                columns: new[] { "payment_request_id", "tenant_id", "bank_connection_id" },
                principalTable: "payment_requests",
                principalColumns: new[] { "id", "tenant_id", "bank_connection_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_payment_requests_bank_connections_tenant_id_bank_connection",
                table: "payment_requests",
                columns: new[] { "tenant_id", "bank_connection_id" },
                principalTable: "bank_connections",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_payment_review_case_members_incoming_transactions_transacti",
                table: "payment_review_case_members",
                columns: new[] { "transaction_id", "tenant_id", "bank_connection_id" },
                principalTable: "incoming_transactions",
                principalColumns: new[] { "id", "tenant_id", "bank_connection_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_payment_review_case_members_message_parse_runs_parse_run_id",
                table: "payment_review_case_members",
                columns: new[] { "parse_run_id", "tenant_id", "bank_connection_id" },
                principalTable: "message_parse_runs",
                principalColumns: new[] { "id", "tenant_id", "bank_connection_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_payment_review_case_members_payment_review_cases_case_id_te",
                table: "payment_review_case_members",
                columns: new[] { "case_id", "tenant_id", "bank_connection_id" },
                principalTable: "payment_review_cases",
                principalColumns: new[] { "id", "tenant_id", "bank_connection_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_payment_review_cases_bank_connections_tenant_id_bank_connec",
                table: "payment_review_cases",
                columns: new[] { "tenant_id", "bank_connection_id" },
                principalTable: "bank_connections",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_transaction_sources_incoming_transactions_transaction_id_te",
                table: "transaction_sources",
                columns: new[] { "transaction_id", "tenant_id", "bank_connection_id" },
                principalTable: "incoming_transactions",
                principalColumns: new[] { "id", "tenant_id", "bank_connection_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_variable_symbol_allocators_bank_connections_bank_connection",
                table: "variable_symbol_allocators",
                column: "bank_connection_id",
                principalTable: "bank_connections",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
