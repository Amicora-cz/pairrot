using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pairrot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialTenants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tenants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    parent_tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    external_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    contact_email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    can_create_subtenants = table.Column<bool>(type: "boolean", nullable: false),
                    can_create_campaigns = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenants", x => x.id);
                    table.CheckConstraint("tenants_deletion_consistent", "(status = 'deleted' AND deleted_at IS NOT NULL) OR (status <> 'deleted' AND deleted_at IS NULL)");
                    table.CheckConstraint("tenants_email_not_blank", "contact_email IS NULL OR btrim(contact_email) <> ''");
                    table.CheckConstraint("tenants_external_id_not_blank", "btrim(external_id) <> ''");
                    table.CheckConstraint("tenants_name_not_blank", "btrim(name) <> ''");
                    table.CheckConstraint("tenants_not_own_parent", "parent_tenant_id IS NULL OR parent_tenant_id <> id");
                    table.CheckConstraint("tenants_status_valid", "status IN ('active', 'suspended', 'deleted')");
                    table.ForeignKey(
                        name: "fk_tenants_tenants_parent_tenant_id",
                        column: x => x.parent_tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tenant_api_keys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purpose = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    secret_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_api_keys", x => x.id);
                    table.CheckConstraint("tenant_api_keys_hash_length", "octet_length(secret_hash) = 32");
                    table.CheckConstraint("tenant_api_keys_purpose_valid", "purpose IN ('tenant', 'management')");
                    table.ForeignKey(
                        name: "fk_tenant_api_keys_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tenant_audit_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_api_key_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    target_tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    details = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_audit_events", x => x.id);
                    table.CheckConstraint("tenant_audit_action_not_blank", "btrim(action) <> ''");
                    table.CheckConstraint("tenant_audit_actor_required", "actor_api_key_id IS NOT NULL OR (actor_description IS NOT NULL AND btrim(actor_description) <> '')");
                    table.CheckConstraint("tenant_audit_details_object", "jsonb_typeof(details) = 'object'");
                    table.ForeignKey(
                        name: "fk_tenant_audit_events_tenant_api_keys_actor_api_key_id",
                        column: x => x.actor_api_key_id,
                        principalTable: "tenant_api_keys",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_tenant_audit_events_tenants_target_tenant_id",
                        column: x => x.target_tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "tenant_api_keys_tenant_idx",
                table: "tenant_api_keys",
                columns: new[] { "tenant_id", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_tenant_audit_events_actor_api_key_id",
                table: "tenant_audit_events",
                column: "actor_api_key_id");

            migrationBuilder.CreateIndex(
                name: "tenant_audit_events_target_idx",
                table: "tenant_audit_events",
                columns: new[] { "target_tenant_id", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "tenants_parent_external_id_unique",
                table: "tenants",
                columns: new[] { "parent_tenant_id", "external_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "tenants_parent_status_idx",
                table: "tenants",
                columns: new[] { "parent_tenant_id", "status", "id" });

            migrationBuilder.CreateIndex(
                name: "tenants_root_external_id_unique",
                table: "tenants",
                column: "external_id",
                unique: true,
                filter: "parent_tenant_id IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tenant_audit_events");

            migrationBuilder.DropTable(
                name: "tenant_api_keys");

            migrationBuilder.DropTable(
                name: "tenants");
        }
    }
}
