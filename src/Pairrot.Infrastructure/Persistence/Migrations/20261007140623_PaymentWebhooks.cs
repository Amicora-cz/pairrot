using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pairrot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PaymentWebhooks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "webhook_endpoints",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    environment = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    destination_revision = table.Column<int>(type: "integer", nullable: false),
                    url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    last_success_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_test_accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_webhook_endpoints", x => x.id);
                    table.CheckConstraint("webhook_endpoints_counters_valid", "destination_revision >= 1 AND version >= 1");
                    table.CheckConstraint("webhook_endpoints_environment_valid", "environment IN ('test', 'live')");
                    table.CheckConstraint("webhook_endpoints_scope_valid", "scope IN ('own', 'own_and_direct_children')");
                    table.CheckConstraint("webhook_endpoints_state_valid", "state IN ('paused', 'active')");
                    table.ForeignKey(
                        name: "fk_webhook_endpoints_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "webhook_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_change_id = table.Column<Guid>(type: "uuid", nullable: true),
                    payment_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    sequence = table.Column<int>(type: "integer", nullable: true),
                    environment = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    synthetic = table.Column<bool>(type: "boolean", nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    body_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_webhook_events", x => x.id);
                    table.CheckConstraint("webhook_events_digest_format", "body_sha256 ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("webhook_events_environment_valid", "environment IN ('test', 'live')");
                    table.CheckConstraint("webhook_events_kind_consistent", "(synthetic AND payment_change_id IS NULL AND payment_request_id IS NULL AND sequence IS NULL AND environment = 'test') OR (NOT synthetic AND payment_change_id IS NOT NULL AND payment_request_id IS NOT NULL AND sequence IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_webhook_events_payment_request_changes_payment_change_id",
                        column: x => x.payment_change_id,
                        principalTable: "payment_request_changes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "webhook_destination_revisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    endpoint_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    created_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_webhook_destination_revisions", x => x.id);
                    table.CheckConstraint("webhook_destination_revisions_revision_valid", "revision >= 1");
                    table.ForeignKey(
                        name: "fk_webhook_destination_revisions_webhook_endpoints_endpoint_id",
                        column: x => x.endpoint_id,
                        principalTable: "webhook_endpoints",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "webhook_signing_keys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    endpoint_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    protected_secret = table.Column<string>(type: "text", nullable: true),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    retires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    retired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_webhook_signing_keys", x => x.id);
                    table.CheckConstraint("webhook_signing_keys_secret_consistent", "(state = 'retired' AND protected_secret IS NULL AND retired_at IS NOT NULL) OR (state = 'current' AND protected_secret IS NOT NULL AND retires_at IS NULL) OR (state = 'retiring' AND protected_secret IS NOT NULL AND retires_at IS NOT NULL)");
                    table.CheckConstraint("webhook_signing_keys_state_valid", "state IN ('current', 'retiring', 'retired')");
                    table.ForeignKey(
                        name: "fk_webhook_signing_keys_webhook_endpoints_endpoint_id",
                        column: x => x.endpoint_id,
                        principalTable: "webhook_endpoints",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "webhook_deliveries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    endpoint_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subscriber_tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    destination_revision = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    hold_reason = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    generation = table.Column<int>(type: "integer", nullable: false),
                    attempt_in_generation = table.Column<int>(type: "integer", nullable: false),
                    total_attempts = table.Column<int>(type: "integer", nullable: false),
                    due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_outcome = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    last_error_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    last_error_message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    last_http_status = table.Column<int>(type: "integer", nullable: true),
                    abandon_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    lease_token = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_owner = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_webhook_deliveries", x => x.id);
                    table.CheckConstraint("webhook_deliveries_counters_valid", "generation >= 1 AND attempt_in_generation BETWEEN 0 AND 4 AND total_attempts >= attempt_in_generation AND version >= 1");
                    table.CheckConstraint("webhook_deliveries_hold_consistent", "(state = 'held') = (hold_reason IS NOT NULL)");
                    table.CheckConstraint("webhook_deliveries_lease_consistent", "(state = 'running' AND lease_token IS NOT NULL AND lease_expires_at IS NOT NULL) OR (state <> 'running' AND lease_token IS NULL AND lease_expires_at IS NULL)");
                    table.CheckConstraint("webhook_deliveries_state_valid", "state IN ('pending', 'running', 'retry_wait', 'delivered', 'failed', 'held', 'abandoned')");
                    table.ForeignKey(
                        name: "fk_webhook_deliveries_webhook_endpoints_endpoint_id",
                        column: x => x.endpoint_id,
                        principalTable: "webhook_endpoints",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_webhook_deliveries_webhook_events_event_id",
                        column: x => x.event_id,
                        principalTable: "webhook_events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "webhook_delivery_attempts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    delivery_id = table.Column<Guid>(type: "uuid", nullable: false),
                    generation = table.Column<int>(type: "integer", nullable: false),
                    attempt_number = table.Column<int>(type: "integer", nullable: false),
                    destination_revision = table.Column<int>(type: "integer", nullable: false),
                    url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    key_ids = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    signed_timestamp = table.Column<long>(type: "bigint", nullable: false),
                    outcome = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    http_status = table.Column<int>(type: "integer", nullable: true),
                    error_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    response_excerpt = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    duration_ms = table.Column<int>(type: "integer", nullable: true),
                    lease_token = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_webhook_delivery_attempts", x => x.id);
                    table.CheckConstraint("webhook_delivery_attempts_number_valid", "attempt_number BETWEEN 1 AND 4");
                    table.CheckConstraint("webhook_delivery_attempts_outcome_valid", "outcome IN ('in_progress', 'delivered', 'retryable_failure', 'permanent_failure', 'outcome_unknown')");
                    table.ForeignKey(
                        name: "fk_webhook_delivery_attempts_webhook_deliveries_delivery_id",
                        column: x => x.delivery_id,
                        principalTable: "webhook_deliveries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "webhook_deliveries_due_idx",
                table: "webhook_deliveries",
                columns: new[] { "state", "due_at" },
                filter: "state IN ('pending', 'retry_wait', 'running')");

            migrationBuilder.CreateIndex(
                name: "webhook_deliveries_endpoint_idx",
                table: "webhook_deliveries",
                columns: new[] { "endpoint_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "webhook_deliveries_event_endpoint_unique",
                table: "webhook_deliveries",
                columns: new[] { "event_id", "endpoint_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "webhook_delivery_attempts_unique",
                table: "webhook_delivery_attempts",
                columns: new[] { "delivery_id", "generation", "attempt_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "webhook_destination_revisions_unique",
                table: "webhook_destination_revisions",
                columns: new[] { "endpoint_id", "revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "webhook_endpoints_tenant_unique",
                table: "webhook_endpoints",
                column: "tenant_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "webhook_events_change_unique",
                table: "webhook_events",
                column: "payment_change_id",
                unique: true,
                filter: "payment_change_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "webhook_events_request_idx",
                table: "webhook_events",
                columns: new[] { "payment_request_id", "sequence" });

            migrationBuilder.CreateIndex(
                name: "webhook_events_tenant_time_idx",
                table: "webhook_events",
                columns: new[] { "tenant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "webhook_signing_keys_key_id_unique",
                table: "webhook_signing_keys",
                column: "key_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "webhook_signing_keys_one_retiring",
                table: "webhook_signing_keys",
                column: "endpoint_id",
                unique: true,
                filter: "state = 'retiring'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "webhook_delivery_attempts");

            migrationBuilder.DropTable(
                name: "webhook_destination_revisions");

            migrationBuilder.DropTable(
                name: "webhook_signing_keys");

            migrationBuilder.DropTable(
                name: "webhook_deliveries");

            migrationBuilder.DropTable(
                name: "webhook_endpoints");

            migrationBuilder.DropTable(
                name: "webhook_events");
        }
    }
}
