using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pairrot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AutomaticVariableSymbols : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "variable_symbol_source",
                table: "payment_requests",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "supplied");

            // Every request created before Feature 012 supplied its symbol; new rows always state their mode.
            migrationBuilder.Sql("ALTER TABLE payment_requests ALTER COLUMN variable_symbol_source DROP DEFAULT;");

            migrationBuilder.AddColumn<long>(
                name: "variable_symbol_number",
                table: "payment_requests",
                type: "bigint",
                nullable: false,
                computedColumnSql: "(variable_symbol_key::bigint)",
                stored: true);

            migrationBuilder.CreateTable(
                name: "variable_symbol_allocators",
                columns: table => new
                {
                    bank_connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    next_candidate = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_variable_symbol_allocators", x => x.bank_connection_id);
                    table.CheckConstraint("variable_symbol_allocators_next_candidate_range", "next_candidate BETWEEN 1 AND 10000000000");
                    table.ForeignKey(
                        name: "fk_variable_symbol_allocators_bank_connections_bank_connection",
                        column: x => x.bank_connection_id,
                        principalTable: "bank_connections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "payment_requests_connection_variable_symbol_number_idx",
                table: "payment_requests",
                columns: new[] { "bank_connection_id", "variable_symbol_number" });

            migrationBuilder.AddCheckConstraint(
                name: "payment_requests_variable_symbol_source_valid",
                table: "payment_requests",
                sql: "variable_symbol_source IN ('supplied', 'automatic')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "variable_symbol_allocators");

            migrationBuilder.DropIndex(
                name: "payment_requests_connection_variable_symbol_number_idx",
                table: "payment_requests");

            migrationBuilder.DropCheckConstraint(
                name: "payment_requests_variable_symbol_source_valid",
                table: "payment_requests");

            migrationBuilder.DropColumn(
                name: "variable_symbol_number",
                table: "payment_requests");

            migrationBuilder.DropColumn(
                name: "variable_symbol_source",
                table: "payment_requests");
        }
    }
}
