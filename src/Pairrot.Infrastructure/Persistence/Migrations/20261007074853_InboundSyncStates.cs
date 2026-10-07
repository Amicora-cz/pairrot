using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pairrot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InboundSyncStates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "inbound_sync_states",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_instance_id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    sync_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    covered_through = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_success_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    next_due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    discovered_count = table.Column<long>(type: "bigint", nullable: false),
                    last_run_discovered_count = table.Column<int>(type: "integer", nullable: false),
                    last_error_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    last_error_message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    coverage_gap = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inbound_sync_states", x => x.id);
                    table.CheckConstraint("inbound_sync_states_counts_valid", "discovered_count >= 0 AND last_run_discovered_count >= 0");
                    table.CheckConstraint("inbound_sync_states_coverage_valid", "covered_through IS NULL OR covered_through >= sync_from");
                });

            migrationBuilder.CreateIndex(
                name: "inbound_sync_states_instance_unique",
                table: "inbound_sync_states",
                column: "provider_instance_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "inbound_sync_states");
        }
    }
}
