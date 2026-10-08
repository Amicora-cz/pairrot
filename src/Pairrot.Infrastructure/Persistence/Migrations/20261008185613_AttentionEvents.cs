using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pairrot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AttentionEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "webhook_events_kind_consistent",
                table: "webhook_events");

            migrationBuilder.AddColumn<Guid>(
                name: "subject_id",
                table: "webhook_events",
                type: "uuid",
                nullable: true);

            // Payment events are about their request; the subject carries their per-request sequence.
            migrationBuilder.Sql("UPDATE webhook_events SET subject_id = payment_request_id WHERE payment_request_id IS NOT NULL;");

            migrationBuilder.AddColumn<string[]>(
                name: "event_types",
                table: "webhook_endpoints",
                type: "text[]",
                nullable: false,
                defaultValueSql: "ARRAY['payment_request.updated']::text[]");

            migrationBuilder.CreateIndex(
                name: "webhook_events_subject_sequence_unique",
                table: "webhook_events",
                columns: new[] { "subject_id", "sequence" },
                unique: true,
                filter: "subject_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "webhook_events_type_idx",
                table: "webhook_events",
                columns: new[] { "type", "id" });

            migrationBuilder.AddCheckConstraint(
                name: "webhook_events_kind_consistent",
                table: "webhook_events",
                sql: "(synthetic AND payment_change_id IS NULL AND payment_request_id IS NULL AND subject_id IS NULL AND sequence IS NULL AND environment = 'test') OR (NOT synthetic AND type = 'payment_request.updated' AND payment_change_id IS NOT NULL AND payment_request_id IS NOT NULL AND subject_id = payment_request_id AND sequence IS NOT NULL) OR (NOT synthetic AND type <> 'payment_request.updated' AND payment_change_id IS NULL AND payment_request_id IS NULL AND subject_id IS NOT NULL AND sequence >= 1)");

            migrationBuilder.AddCheckConstraint(
                name: "webhook_endpoints_event_types_valid",
                table: "webhook_endpoints",
                sql: "cardinality(event_types) >= 1 AND event_types <@ ARRAY['payment_request.updated', 'transaction.recorded', 'transaction.updated', 'review_case.opened', 'review_case.updated', 'inbound_address.paused', 'inbound_address.resumed']::text[]");

            migrationBuilder.CreateIndex(
                name: "payment_requests_overpaid_idx",
                table: "payment_requests",
                column: "tenant_id",
                filter: "paid_amount > amount");

            migrationBuilder.CreateIndex(
                name: "incoming_transactions_unallocated_idx",
                table: "incoming_transactions",
                columns: new[] { "tenant_id", "created_at" },
                filter: "handling = 'unallocated'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "webhook_events_subject_sequence_unique",
                table: "webhook_events");

            migrationBuilder.DropIndex(
                name: "webhook_events_type_idx",
                table: "webhook_events");

            migrationBuilder.DropCheckConstraint(
                name: "webhook_events_kind_consistent",
                table: "webhook_events");

            migrationBuilder.DropCheckConstraint(
                name: "webhook_endpoints_event_types_valid",
                table: "webhook_endpoints");

            migrationBuilder.DropIndex(
                name: "payment_requests_overpaid_idx",
                table: "payment_requests");

            migrationBuilder.DropIndex(
                name: "incoming_transactions_unallocated_idx",
                table: "incoming_transactions");

            migrationBuilder.DropColumn(
                name: "subject_id",
                table: "webhook_events");

            migrationBuilder.DropColumn(
                name: "event_types",
                table: "webhook_endpoints");

            migrationBuilder.AddCheckConstraint(
                name: "webhook_events_kind_consistent",
                table: "webhook_events",
                sql: "(synthetic AND payment_change_id IS NULL AND payment_request_id IS NULL AND sequence IS NULL AND environment = 'test') OR (NOT synthetic AND payment_change_id IS NOT NULL AND payment_request_id IS NOT NULL AND sequence IS NOT NULL)");
        }
    }
}
