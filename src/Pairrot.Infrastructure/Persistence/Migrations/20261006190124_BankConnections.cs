using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pairrot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BankConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "bank_connections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    account_number = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    iban = table.Column<string>(type: "character varying(34)", maxLength: 34, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    email_provider = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    inbound_address = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    address_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    address_created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bank_connections", x => x.id);
                    table.CheckConstraint("bank_connections_account_number_not_blank", "btrim(account_number) <> ''");
                    table.CheckConstraint("bank_connections_address_consistent", "(address_status = 'not_created' AND inbound_address IS NULL AND address_created_at IS NULL AND email_provider IS NULL) OR (address_status = 'stub_created' AND inbound_address IS NOT NULL AND address_created_at IS NOT NULL AND email_provider = 'stub')");
                    table.CheckConstraint("bank_connections_address_status_valid", "address_status IN ('not_created', 'stub_created')");
                    table.CheckConstraint("bank_connections_archive_consistent", "(status = 'archived' AND archived_at IS NOT NULL) OR (status <> 'archived' AND archived_at IS NULL)");
                    table.CheckConstraint("bank_connections_currency_valid", "currency = 'CZK'");
                    table.CheckConstraint("bank_connections_iban_format", "iban ~ '^CZ[0-9]{22}$'");
                    table.CheckConstraint("bank_connections_label_not_blank", "label IS NULL OR btrim(label) <> ''");
                    table.CheckConstraint("bank_connections_status_valid", "status IN ('active', 'archived')");
                    table.ForeignKey(
                        name: "fk_bank_connections_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "bank_connections_iban_unique",
                table: "bank_connections",
                column: "iban",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "bank_connections_inbound_address_unique",
                table: "bank_connections",
                column: "inbound_address",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "bank_connections_tenant_idx",
                table: "bank_connections",
                columns: new[] { "tenant_id", "status", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bank_connections");
        }
    }
}
