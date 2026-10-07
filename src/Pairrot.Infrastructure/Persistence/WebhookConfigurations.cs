using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pairrot.Core.Inbound;
using Pairrot.Core.Matching;
using Pairrot.Core.Tenants;
using Pairrot.Core.Webhooks;

namespace Pairrot.Infrastructure.Persistence;

// Features 010 and 011: outgoing payment-status webhooks. Nothing here is ever hard-deleted.

internal sealed class WebhookEndpointConfiguration : IEntityTypeConfiguration<WebhookEndpoint>
{
    public const string TenantUniqueIndex = "webhook_endpoints_tenant_unique";

    public void Configure(EntityTypeBuilder<WebhookEndpoint> builder)
    {
        builder.ToTable("webhook_endpoints", t =>
        {
            t.HasCheckConstraint("webhook_endpoints_scope_valid", $"scope IN ({SnakeCaseEnum<WebhookScope>.SqlList})");
            t.HasCheckConstraint("webhook_endpoints_state_valid", $"state IN ({SnakeCaseEnum<WebhookEndpointState>.SqlList})");
            t.HasCheckConstraint("webhook_endpoints_environment_valid", "environment IN ('test', 'live')");
            t.HasCheckConstraint("webhook_endpoints_counters_valid", "destination_revision >= 1 AND version >= 1");
        });

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.Scope).HasConversion<SnakeCaseEnumConverter<WebhookScope>>().HasMaxLength(30);
        builder.Property(e => e.State).HasConversion<SnakeCaseEnumConverter<WebhookEndpointState>>().HasMaxLength(20);
        builder.Property(e => e.Environment).HasConversion<LowercaseEnumConverter<InboundEnvironment>>().HasMaxLength(10);
        builder.Property(e => e.Url).HasMaxLength(WebhookDestinations.MaxUrlLength);
        builder.Property(e => e.Version).IsConcurrencyToken();

        builder.HasOne<Tenant>().WithMany().HasForeignKey(e => e.TenantId).OnDelete(DeleteBehavior.Restrict);

        // One endpoint per tenant.
        builder.HasIndex(e => e.TenantId).IsUnique().HasDatabaseName(TenantUniqueIndex);
    }
}

internal sealed class WebhookDestinationRevisionConfiguration : IEntityTypeConfiguration<WebhookDestinationRevision>
{
    public void Configure(EntityTypeBuilder<WebhookDestinationRevision> builder)
    {
        builder.ToTable("webhook_destination_revisions", t => t.HasCheckConstraint("webhook_destination_revisions_revision_valid", "revision >= 1"));
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.Url).HasMaxLength(WebhookDestinations.MaxUrlLength);
        builder.Property(r => r.CreatedBy).HasMaxLength(200);
        builder.HasOne<WebhookEndpoint>().WithMany().HasForeignKey(r => r.EndpointId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(r => new { r.EndpointId, r.Revision }).IsUnique().HasDatabaseName("webhook_destination_revisions_unique");
    }
}

internal sealed class WebhookSigningKeyConfiguration : IEntityTypeConfiguration<WebhookSigningKey>
{
    public void Configure(EntityTypeBuilder<WebhookSigningKey> builder)
    {
        builder.ToTable("webhook_signing_keys", t =>
        {
            t.HasCheckConstraint("webhook_signing_keys_state_valid", $"state IN ({SnakeCaseEnum<WebhookKeyState>.SqlList})");
            t.HasCheckConstraint(
                "webhook_signing_keys_secret_consistent",
                "(state = 'retired' AND protected_secret IS NULL AND retired_at IS NOT NULL) "
                + "OR (state = 'current' AND protected_secret IS NOT NULL AND retires_at IS NULL) "
                + "OR (state = 'retiring' AND protected_secret IS NOT NULL AND retires_at IS NOT NULL)");
        });

        builder.HasKey(k => k.Id);
        builder.Property(k => k.Id).ValueGeneratedNever();
        builder.Property(k => k.KeyId).HasMaxLength(40);
        builder.Property(k => k.State).HasConversion<SnakeCaseEnumConverter<WebhookKeyState>>().HasMaxLength(20);
        builder.HasOne<WebhookEndpoint>().WithMany().HasForeignKey(k => k.EndpointId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(k => k.KeyId).IsUnique().HasDatabaseName("webhook_signing_keys_key_id_unique");
        builder.HasIndex(k => k.EndpointId).IsUnique().HasFilter("state = 'current'").HasDatabaseName("webhook_signing_keys_one_current");
        builder.HasIndex(k => k.EndpointId).IsUnique().HasFilter("state = 'retiring'").HasDatabaseName("webhook_signing_keys_one_retiring");
    }
}

internal sealed class WebhookEventConfiguration : IEntityTypeConfiguration<WebhookEvent>
{
    public const string ChangeUniqueIndex = "webhook_events_change_unique";

    public void Configure(EntityTypeBuilder<WebhookEvent> builder)
    {
        builder.ToTable("webhook_events", t =>
        {
            t.HasCheckConstraint("webhook_events_environment_valid", "environment IN ('test', 'live')");
            t.HasCheckConstraint("webhook_events_kind_consistent",
                "(synthetic AND payment_change_id IS NULL AND payment_request_id IS NULL AND sequence IS NULL AND environment = 'test') "
                + "OR (NOT synthetic AND payment_change_id IS NOT NULL AND payment_request_id IS NOT NULL AND sequence IS NOT NULL)");
            t.HasCheckConstraint("webhook_events_digest_format", "body_sha256 ~ '^[0-9a-f]{64}$'");
        });

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.Type).HasMaxLength(50);
        builder.Property(e => e.Environment).HasConversion<LowercaseEnumConverter<InboundEnvironment>>().HasMaxLength(10);
        builder.Property(e => e.BodySha256).HasMaxLength(64);

        builder.HasOne<PaymentChange>().WithMany().HasForeignKey(e => e.PaymentChangeId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(e => e.PaymentChangeId).IsUnique().HasFilter("payment_change_id IS NOT NULL").HasDatabaseName(ChangeUniqueIndex);
        builder.HasIndex(e => new { e.TenantId, e.CreatedAt }).HasDatabaseName("webhook_events_tenant_time_idx");
        builder.HasIndex(e => new { e.PaymentRequestId, e.Sequence }).HasDatabaseName("webhook_events_request_idx");
    }
}

internal sealed class WebhookDeliveryConfiguration : IEntityTypeConfiguration<WebhookDelivery>
{
    public const string PairUniqueIndex = "webhook_deliveries_event_endpoint_unique";

    public void Configure(EntityTypeBuilder<WebhookDelivery> builder)
    {
        builder.ToTable("webhook_deliveries", t =>
        {
            t.HasCheckConstraint("webhook_deliveries_state_valid", $"state IN ({SnakeCaseEnum<WebhookDeliveryState>.SqlList})");
            t.HasCheckConstraint("webhook_deliveries_counters_valid",
                $"generation >= 1 AND attempt_in_generation BETWEEN 0 AND {WebhookRetryPolicy.MaxAttempts} AND total_attempts >= attempt_in_generation AND version >= 1");
            t.HasCheckConstraint("webhook_deliveries_lease_consistent",
                "(state = 'running' AND lease_token IS NOT NULL AND lease_expires_at IS NOT NULL) "
                + "OR (state <> 'running' AND lease_token IS NULL AND lease_expires_at IS NULL)");
            t.HasCheckConstraint("webhook_deliveries_hold_consistent", "(state = 'held') = (hold_reason IS NOT NULL)");
        });

        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedNever();
        builder.Property(d => d.State).HasConversion<SnakeCaseEnumConverter<WebhookDeliveryState>>().HasMaxLength(20);
        builder.Property(d => d.HoldReason).HasMaxLength(50);
        builder.Property(d => d.LastOutcome).HasMaxLength(30);
        builder.Property(d => d.LastErrorCode).HasMaxLength(50);
        builder.Property(d => d.LastErrorMessage).HasMaxLength(500);
        builder.Property(d => d.AbandonReason).HasMaxLength(MatchingRules.MaxReasonLength);
        builder.Property(d => d.LeaseOwner).HasMaxLength(200);
        builder.Property(d => d.Version).IsConcurrencyToken();

        builder.HasOne<WebhookEvent>().WithMany().HasForeignKey(d => d.EventId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<WebhookEndpoint>().WithMany().HasForeignKey(d => d.EndpointId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(d => new { d.EventId, d.EndpointId }).IsUnique().HasDatabaseName(PairUniqueIndex);
        builder.HasIndex(d => new { d.State, d.DueAt })
            .HasFilter("state IN ('pending', 'retry_wait', 'running')")
            .HasDatabaseName("webhook_deliveries_due_idx");
        builder.HasIndex(d => new { d.EndpointId, d.CreatedAt }).HasDatabaseName("webhook_deliveries_endpoint_idx");
    }
}

internal sealed class WebhookDeliveryAttemptConfiguration : IEntityTypeConfiguration<WebhookDeliveryAttempt>
{
    public void Configure(EntityTypeBuilder<WebhookDeliveryAttempt> builder)
    {
        builder.ToTable("webhook_delivery_attempts", t =>
        {
            t.HasCheckConstraint("webhook_delivery_attempts_outcome_valid", $"outcome IN ({SnakeCaseEnum<WebhookAttemptOutcome>.SqlList})");
            t.HasCheckConstraint("webhook_delivery_attempts_number_valid", $"attempt_number BETWEEN 1 AND {WebhookRetryPolicy.MaxAttempts}");
        });

        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.Url).HasMaxLength(WebhookDestinations.MaxUrlLength);
        builder.Property(a => a.KeyIds).HasMaxLength(100);
        builder.Property(a => a.Outcome).HasConversion<SnakeCaseEnumConverter<WebhookAttemptOutcome>>().HasMaxLength(30);
        builder.Property(a => a.ErrorCode).HasMaxLength(50);
        builder.Property(a => a.ResponseExcerpt).HasMaxLength(1000);

        builder.HasOne<WebhookDelivery>().WithMany().HasForeignKey(a => a.DeliveryId).OnDelete(DeleteBehavior.Restrict);

        // An attempt number is reserved once per generation, so the budget cannot be exceeded.
        builder.HasIndex(a => new { a.DeliveryId, a.Generation, a.AttemptNumber }).IsUnique().HasDatabaseName("webhook_delivery_attempts_unique");
    }
}
