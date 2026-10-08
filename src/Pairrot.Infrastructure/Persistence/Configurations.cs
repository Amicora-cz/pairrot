using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pairrot.Core.Tenants;
using Pairrot.Infrastructure.Users;

namespace Pairrot.Infrastructure.Persistence;

// Tenant tables mirror docs/features/001-tenants-schema.sql; user tables are added by Feature 002
// (see UserConfigurations.cs).

internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("tenants", t =>
        {
            t.HasCheckConstraint("tenants_not_own_parent", "parent_tenant_id IS NULL OR parent_tenant_id <> id");
            t.HasCheckConstraint("tenants_external_id_not_blank", "btrim(external_id) <> ''");
            t.HasCheckConstraint("tenants_name_not_blank", "btrim(name) <> ''");
            t.HasCheckConstraint("tenants_email_not_blank", "contact_email IS NULL OR btrim(contact_email) <> ''");
            t.HasCheckConstraint("tenants_status_valid", "status IN ('active', 'suspended', 'deleted')");
            t.HasCheckConstraint(
                "tenants_deletion_consistent",
                "(status = 'deleted' AND deleted_at IS NOT NULL) OR (status <> 'deleted' AND deleted_at IS NULL)");

            // Feature 014: only a deleted tenant is erased, and it is erased only after the request.
            t.HasCheckConstraint(
                "tenants_erasure_consistent",
                "(erasure_requested_at IS NULL AND erased_at IS NULL) "
                + "OR (erasure_requested_at IS NOT NULL AND status = 'deleted' AND (erased_at IS NULL OR erased_at >= erasure_requested_at))");
        });

        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.ExternalId).HasMaxLength(TenantRules.MaxExternalIdLength);
        builder.Property(t => t.Name).HasMaxLength(TenantRules.MaxNameLength);
        builder.Property(t => t.ContactEmail).HasMaxLength(TenantRules.MaxEmailLength);
        builder.Property(t => t.Status)
            .HasConversion<LowercaseEnumConverter<TenantStatus>>()
            .HasMaxLength(20);

        // Optimistic concurrency on the PostgreSQL system row version.
        builder.Property<uint>("xmin").IsRowVersion();

        builder.Ignore(t => t.IsRoot);

        builder.HasOne(t => t.ParentTenant)
            .WithMany()
            .HasForeignKey(t => t.ParentTenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(t => new { t.ParentTenantId, t.ExternalId })
            .IsUnique()
            .HasDatabaseName("tenants_parent_external_id_unique");

        // Ordinary UNIQUE permits multiple NULL parents, so roots need their own index.
        builder.HasIndex(t => t.ExternalId)
            .IsUnique()
            .HasFilter("parent_tenant_id IS NULL")
            .HasDatabaseName("tenants_root_external_id_unique");

        builder.HasIndex(t => new { t.ParentTenantId, t.Status, t.Id })
            .HasDatabaseName("tenants_parent_status_idx");
    }
}

internal sealed class TenantApiKeyConfiguration : IEntityTypeConfiguration<TenantApiKey>
{
    public void Configure(EntityTypeBuilder<TenantApiKey> builder)
    {
        builder.ToTable("tenant_api_keys", t =>
        {
            t.HasCheckConstraint("tenant_api_keys_purpose_valid", "purpose IN ('tenant', 'management')");
            t.HasCheckConstraint("tenant_api_keys_hash_length", "octet_length(secret_hash) = 32");
        });

        builder.HasKey(k => k.Id);
        builder.Property(k => k.Id).ValueGeneratedNever();
        builder.Property(k => k.Purpose).HasConversion<LowercaseEnumConverter<ApiKeyPurpose>>().HasMaxLength(20);
        builder.Property(k => k.Label).HasMaxLength(TenantRules.MaxLabelLength);

        builder.HasOne(k => k.Tenant)
            .WithMany(t => t.ApiKeys)
            .HasForeignKey(k => k.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(k => new { k.TenantId, k.CreatedAt, k.Id }).HasDatabaseName("tenant_api_keys_tenant_idx");
    }
}

internal sealed class TenantAuditEventConfiguration : IEntityTypeConfiguration<TenantAuditEvent>
{
    public void Configure(EntityTypeBuilder<TenantAuditEvent> builder)
    {
        builder.ToTable("tenant_audit_events", t =>
        {
            // Exactly one actor: a signed-in user, an API key, or a named administrative operation.
            t.HasCheckConstraint(
                "tenant_audit_actor_exactly_one",
                "num_nonnulls(actor_user_id, actor_api_key_id, actor_description) = 1 "
                + "AND (actor_description IS NULL OR btrim(actor_description) <> '')");
            t.HasCheckConstraint("tenant_audit_action_not_blank", "btrim(action) <> ''");
            t.HasCheckConstraint("tenant_audit_details_object", "jsonb_typeof(details) = 'object'");
        });

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.ActorDescription).HasMaxLength(200);
        builder.Property(e => e.Action).HasMaxLength(100);
        builder.Property(e => e.Details).HasColumnType("jsonb");

        builder.HasOne<TenantApiKey>()
            .WithMany()
            .HasForeignKey(e => e.ActorApiKeyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<PairrotUser>()
            .WithMany()
            .HasForeignKey(e => e.ActorUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(e => e.TargetTenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(e => new { e.TargetTenantId, e.CreatedAt, e.Id })
            .HasDatabaseName("tenant_audit_events_target_idx");
    }
}

internal sealed class TenantErasureConfiguration : IEntityTypeConfiguration<TenantErasure>
{
    public const string TenantUniqueIndex = "tenant_erasures_tenant_unique";

    public void Configure(EntityTypeBuilder<TenantErasure> builder)
    {
        builder.ToTable("tenant_erasures", t =>
        {
            t.HasCheckConstraint("tenant_erasures_state_valid", $"state IN ({SnakeCaseEnum<TenantErasureState>.SqlList})");
            t.HasCheckConstraint(
                "tenant_erasures_completion_consistent",
                "(state = 'done' AND completed_at IS NOT NULL AND reason IS NULL) OR (state <> 'done' AND completed_at IS NULL)");
            t.HasCheckConstraint("tenant_erasures_counts_object", "jsonb_typeof(counts) = 'object'");
            t.HasCheckConstraint("tenant_erasures_limitations_array", "jsonb_typeof(limitations) = 'array'");
            t.HasCheckConstraint(
                "tenant_erasures_key_digest_format", "idempotency_key_sha256 IS NULL OR idempotency_key_sha256 ~ '^[0-9a-f]{64}$'");
        });

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.State).HasConversion<SnakeCaseEnumConverter<TenantErasureState>>().HasMaxLength(20);
        builder.Property(e => e.RequestedBy).HasMaxLength(200);
        builder.Property(e => e.Reason).HasMaxLength(Core.Matching.MatchingRules.MaxReasonLength);
        builder.Property(e => e.IdempotencyKeySha256).HasMaxLength(64);
        builder.Property(e => e.Counts).HasColumnType("jsonb");
        builder.Property(e => e.Limitations).HasColumnType("jsonb");

        builder.HasOne<Tenant>().WithMany().HasForeignKey(e => e.TenantId).OnDelete(DeleteBehavior.Restrict);

        // One erasure per tenant: a second request returns the first.
        builder.HasIndex(e => e.TenantId).IsUnique().HasDatabaseName(TenantUniqueIndex);
    }
}
