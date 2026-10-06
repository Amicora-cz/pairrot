using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pairrot.Core.Tenants;
using Pairrot.Core.Users;
using Pairrot.Infrastructure.Users;

namespace Pairrot.Infrastructure.Persistence;

// Feature 002: users (ASP.NET Core Identity storage), memberships, sessions, and user audit events.

internal sealed class PairrotUserConfiguration : IEntityTypeConfiguration<PairrotUser>
{
    public void Configure(EntityTypeBuilder<PairrotUser> builder)
    {
        builder.ToTable("users");
        builder.Property(u => u.Id).ValueGeneratedNever();
        builder.Ignore(u => u.IsDisabled);

        // The login email is unique after normalization (it is also the Identity user name).
        builder.HasIndex(u => u.NormalizedEmail).IsUnique().HasDatabaseName("users_normalized_email_unique");
    }
}

internal sealed class UserClaimConfiguration : IEntityTypeConfiguration<IdentityUserClaim<Guid>>
{
    public void Configure(EntityTypeBuilder<IdentityUserClaim<Guid>> builder) => builder.ToTable("user_claims");
}

internal sealed class UserLoginConfiguration : IEntityTypeConfiguration<IdentityUserLogin<Guid>>
{
    public void Configure(EntityTypeBuilder<IdentityUserLogin<Guid>> builder) => builder.ToTable("user_logins");
}

internal sealed class UserTokenConfiguration : IEntityTypeConfiguration<IdentityUserToken<Guid>>
{
    public void Configure(EntityTypeBuilder<IdentityUserToken<Guid>> builder) => builder.ToTable("user_tokens");
}

internal sealed class TenantMembershipConfiguration : IEntityTypeConfiguration<TenantMembership>
{
    public void Configure(EntityTypeBuilder<TenantMembership> builder)
    {
        builder.ToTable("tenant_memberships", t =>
            t.HasCheckConstraint("tenant_memberships_role_valid", "role IN ('admin')"));

        // One membership per user and tenant; reactivation updates the same row.
        builder.HasKey(m => new { m.UserId, m.TenantId });
        builder.Property(m => m.Role).HasMaxLength(20);
        builder.Ignore(m => m.IsActive);

        builder.HasOne<PairrotUser>()
            .WithMany()
            .HasForeignKey(m => m.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(m => m.Tenant)
            .WithMany()
            .HasForeignKey(m => m.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(m => m.TenantId).HasDatabaseName("tenant_memberships_tenant_idx");
    }
}

internal sealed class UserSessionConfiguration : IEntityTypeConfiguration<UserSession>
{
    public void Configure(EntityTypeBuilder<UserSession> builder)
    {
        builder.ToTable("user_sessions", t =>
        {
            t.HasCheckConstraint("user_sessions_expiry_order", "idle_expires_at <= absolute_expires_at");
            t.HasCheckConstraint(
                "user_sessions_revocation_consistent",
                "(revoked_at IS NULL AND revocation_reason IS NULL) OR (revoked_at IS NOT NULL AND revocation_reason IS NOT NULL)");
        });

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.RevocationReason).HasMaxLength(50);

        builder.HasOne<PairrotUser>()
            .WithMany()
            .HasForeignKey(s => s.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(s => s.SelectedTenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(s => s.UserId)
            .HasFilter("revoked_at IS NULL")
            .HasDatabaseName("user_sessions_active_user_idx");
    }
}

internal sealed class UserAuditEventConfiguration : IEntityTypeConfiguration<UserAuditEvent>
{
    public void Configure(EntityTypeBuilder<UserAuditEvent> builder)
    {
        builder.ToTable("user_audit_events", t =>
        {
            t.HasCheckConstraint(
                "user_audit_actor_exactly_one",
                "num_nonnulls(actor_user_id, actor_description) = 1 "
                + "AND (actor_description IS NULL OR btrim(actor_description) <> '')");
            t.HasCheckConstraint("user_audit_action_not_blank", "btrim(action) <> ''");
            t.HasCheckConstraint("user_audit_details_object", "jsonb_typeof(details) = 'object'");
        });

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.ActorDescription).HasMaxLength(200);
        builder.Property(e => e.Action).HasMaxLength(100);
        builder.Property(e => e.Details).HasColumnType("jsonb");

        builder.HasOne<PairrotUser>()
            .WithMany()
            .HasForeignKey(e => e.TargetUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<PairrotUser>()
            .WithMany()
            .HasForeignKey(e => e.ActorUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(e => new { e.TargetUserId, e.CreatedAt, e.Id }).HasDatabaseName("user_audit_events_target_idx");
    }
}
