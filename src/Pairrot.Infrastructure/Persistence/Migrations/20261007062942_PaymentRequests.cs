using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pairrot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PaymentRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "ak_bank_connections_tenant_id_id",
                table: "bank_connections",
                columns: new[] { "tenant_id", "id" });

            migrationBuilder.CreateTable(
                name: "payment_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bank_connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    account_number = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    iban = table.Column<string>(type: "character varying(34)", maxLength: 34, nullable: false),
                    amount = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    variable_symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    variable_symbol_key = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    message = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    cancellation_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    creation_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    qr_payload = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    qr_format = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    qr_generator_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_requests", x => x.id);
                    table.CheckConstraint("payment_requests_amount_range", "amount BETWEEN 1 AND 999999999");
                    table.CheckConstraint("payment_requests_cancellation_consistent", "(status = 'cancelled' AND cancelled_at IS NOT NULL) OR (status <> 'cancelled' AND cancelled_at IS NULL AND cancellation_reason IS NULL)");
                    table.CheckConstraint("payment_requests_currency_valid", "currency = 'CZK'");
                    table.CheckConstraint("payment_requests_external_id_not_blank", "btrim(external_id) <> ''");
                    table.CheckConstraint("payment_requests_fingerprint_format", "creation_fingerprint ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("payment_requests_iban_format", "iban ~ '^CZ[0-9]{22}$'");
                    table.CheckConstraint("payment_requests_qr_format_valid", "qr_format = 'SPAYD'");
                    table.CheckConstraint("payment_requests_status_valid", "status IN ('open', 'cancelled')");
                    table.CheckConstraint("payment_requests_variable_symbol_format", "variable_symbol ~ '^[0-9]{1,10}$'");
                    table.CheckConstraint("payment_requests_variable_symbol_key_format", "variable_symbol_key ~ '^(0|[1-9][0-9]{0,9})$'");
                    table.CheckConstraint("payment_requests_version_positive", "version >= 1");
                    table.ForeignKey(
                        name: "fk_payment_requests_bank_connections_tenant_id_bank_connection",
                        columns: x => new { x.tenant_id, x.bank_connection_id },
                        principalTable: "bank_connections",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payment_requests_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_payment_requests_tenant_id_bank_connection_id",
                table: "payment_requests",
                columns: new[] { "tenant_id", "bank_connection_id" });

            migrationBuilder.CreateIndex(
                name: "payment_requests_connection_variable_symbol_unique",
                table: "payment_requests",
                columns: new[] { "bank_connection_id", "variable_symbol_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "payment_requests_tenant_due_idx",
                table: "payment_requests",
                columns: new[] { "tenant_id", "status", "due_at" });

            migrationBuilder.CreateIndex(
                name: "payment_requests_tenant_external_id_unique",
                table: "payment_requests",
                columns: new[] { "tenant_id", "external_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "payment_requests_tenant_idx",
                table: "payment_requests",
                columns: new[] { "tenant_id", "id" });

            migrationBuilder.CreateIndex(
                name: "payment_requests_tenant_status_idx",
                table: "payment_requests",
                columns: new[] { "tenant_id", "status", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "payment_requests");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_bank_connections_tenant_id_id",
                table: "bank_connections");
        }
    }
}
