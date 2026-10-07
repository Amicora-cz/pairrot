using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pairrot.Core.Banking;
using Pairrot.Core.Inbound;
using Pairrot.Core.Jobs;
using Pairrot.Core.Parsing;
using Pairrot.Core.Tenants;
using Pairrot.Core.Transactions;

namespace Pairrot.Infrastructure.Persistence;

// Feature 006: notification addresses, delivery receipts, received messages, and durable jobs.
// Nothing here names a provider: instances are configured identities, kinds are free values.

internal sealed class InboundAddressConfiguration : IEntityTypeConfiguration<InboundAddress>
{
    public const string AddressUniqueIndex = "inbound_addresses_address_unique";
    public const string OneActivePerConnectionIndex = "inbound_addresses_one_active_per_connection";

    // Mirrors InboundEmailOptions.IsReservedTestDomain: an address on a test domain is never live.
    private const string ReservedDomain = "'@([a-z0-9-]+\\.)*[a-z0-9-]+\\.(test|example|invalid|localhost)$'";

    public void Configure(EntityTypeBuilder<InboundAddress> builder)
    {
        builder.ToTable("inbound_addresses", t =>
        {
            t.HasCheckConstraint("inbound_addresses_address_normalized", "address = lower(btrim(address)) AND address LIKE '_%@_%'");
            t.HasCheckConstraint("inbound_addresses_environment_valid", "environment IN ('test', 'live')");
            t.HasCheckConstraint("inbound_addresses_live_not_on_test_domain", $"environment = 'test' OR address !~ {ReservedDomain}");
            t.HasCheckConstraint("inbound_addresses_state_valid", "state IN ('active', 'retired')");
            t.HasCheckConstraint("inbound_addresses_provisioning_valid", "provisioning_state IN ('pending', 'ready', 'failed')");
            t.HasCheckConstraint(
                "inbound_addresses_provisioning_consistent",
                "(provisioning_state = 'ready' AND ready_at IS NOT NULL) OR (provisioning_state <> 'ready' AND ready_at IS NULL)");
            t.HasCheckConstraint(
                "inbound_addresses_retirement_consistent",
                "(state = 'retired' AND retired_at IS NOT NULL) OR (state = 'active' AND retired_at IS NULL)");
        });

        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.Address).HasMaxLength(EmailAddresses.MaxLength);
        builder.Property(a => a.ProviderInstanceId).HasMaxLength(50);
        builder.Property(a => a.ProviderKind).HasMaxLength(50);
        builder.Property(a => a.Environment).HasConversion<LowercaseEnumConverter<InboundEnvironment>>().HasMaxLength(10);
        builder.Property(a => a.ProvisioningState).HasConversion<LowercaseEnumConverter<InboundProvisioningState>>().HasMaxLength(20);
        builder.Property(a => a.ProvisioningError).HasMaxLength(500);
        builder.Property(a => a.State).HasConversion<LowercaseEnumConverter<InboundAddressState>>().HasMaxLength(20);

        builder.HasOne<Tenant>().WithMany().HasForeignKey(a => a.TenantId).OnDelete(DeleteBehavior.Restrict);

        // The connection must belong to the same tenant as the address.
        builder.HasOne<BankConnection>()
            .WithMany()
            .HasForeignKey(a => new { a.TenantId, a.BankConnectionId })
            .HasPrincipalKey(b => new { b.TenantId, b.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Lets messages reference an address together with its owner (see InboundMessageConfiguration).
        builder.HasAlternateKey(a => new { a.Id, a.TenantId, a.BankConnectionId });

        // Retired addresses stay reserved forever.
        builder.HasIndex(a => a.Address).IsUnique().HasDatabaseName(AddressUniqueIndex);

        // A pending reservation counts as the active address: it is retried, never duplicated.
        builder.HasIndex(a => a.BankConnectionId)
            .IsUnique()
            .HasFilter("state = 'active'")
            .HasDatabaseName(OneActivePerConnectionIndex);
    }
}

internal sealed class InboundDeliveryReceiptConfiguration : IEntityTypeConfiguration<InboundDeliveryReceipt>
{
    public const string DeliveryUniqueIndex = "inbound_delivery_receipts_delivery_unique";

    public void Configure(EntityTypeBuilder<InboundDeliveryReceipt> builder)
    {
        builder.ToTable("inbound_delivery_receipts", t =>
        {
            t.HasCheckConstraint("inbound_delivery_receipts_digest_format", "payload_sha256 ~ '^[0-9a-f]{64}$'");
            t.HasCheckConstraint(
                "inbound_delivery_receipts_outcome_valid",
                $"outcome IN ({SnakeCaseEnum<InboundReceiptOutcome>.SqlList})");
        });

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.ProviderInstanceId).HasMaxLength(50);
        builder.Property(r => r.SourceDeliveryId).HasMaxLength(200);
        builder.Property(r => r.TransportKind).HasMaxLength(50);
        builder.Property(r => r.DeliveryEvidence).HasMaxLength(100);
        builder.Property(r => r.PayloadSha256).HasMaxLength(64);
        builder.Property(r => r.Outcome).HasConversion<SnakeCaseEnumConverter<InboundReceiptOutcome>>().HasMaxLength(30);

        builder.HasOne<InboundMessage>().WithMany().HasForeignKey(r => r.InboundMessageId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(r => new { r.ProviderInstanceId, r.SourceDeliveryId }).IsUnique().HasDatabaseName(DeliveryUniqueIndex);
        builder.HasIndex(r => r.InboundMessageId).HasDatabaseName("inbound_delivery_receipts_message_idx");
    }
}

internal sealed class InboundMessageConfiguration : IEntityTypeConfiguration<InboundMessage>
{
    public const string SourceMessageUniqueIndex = "inbound_messages_source_message_unique";

    private static readonly string AuthenticationValues = string.Join(", ", AuthenticationResults.Values.Order().Select(v => $"'{v}'"));

    public void Configure(EntityTypeBuilder<InboundMessage> builder)
    {
        builder.ToTable("inbound_messages", t =>
        {
            t.HasCheckConstraint("inbound_messages_source_kind_valid", "source_kind IN ('received', 'fixture')");
            t.HasCheckConstraint("inbound_messages_environment_valid", "environment IN ('test', 'live')");
            t.HasCheckConstraint(
                "inbound_messages_routing_state_valid",
                $"routing_state IN ({SnakeCaseEnum<InboundRoutingState>.SqlList})");
            t.HasCheckConstraint(
                "inbound_messages_hold_reason_valid",
                $"hold_reason IS NULL OR hold_reason IN ({SnakeCaseEnum<InboundHoldReason>.SqlList})");
            t.HasCheckConstraint(
                "inbound_messages_content_state_valid",
                $"content_state IN ({SnakeCaseEnum<InboundContentState>.SqlList})");
            t.HasCheckConstraint(
                "inbound_messages_content_mode_consistent",
                "(content_mode = 'deferred' AND content_reference IS NOT NULL) OR (content_mode = 'inline' AND content_reference IS NULL)");
            t.HasCheckConstraint(
                "inbound_messages_authentication_valid",
                $"spf_result IN ({AuthenticationValues}) AND dkim_result IN ({AuthenticationValues}) AND dmarc_result IN ({AuthenticationValues})");

            // An unresolved message has no owner at all; a routed one has a complete owner.
            t.HasCheckConstraint(
                "inbound_messages_routing_consistent",
                "(routing_state = 'routed' AND tenant_id IS NOT NULL AND bank_connection_id IS NOT NULL AND inbound_address_id IS NOT NULL) "
                + "OR (routing_state <> 'routed' AND tenant_id IS NULL AND bank_connection_id IS NULL AND inbound_address_id IS NULL "
                + "AND hold_reason IS NULL)");
            t.HasCheckConstraint(
                "inbound_messages_content_consistent",
                "(content_state = 'available' AND content_sha256 IS NOT NULL AND content_stored_at IS NOT NULL) "
                + "OR (content_state = 'pending' AND content_sha256 IS NULL AND content_stored_at IS NULL) "
                + "OR (content_state = 'failed' AND content_sha256 IS NULL AND content_stored_at IS NULL AND fetch_error_code IS NOT NULL)");
            t.HasCheckConstraint("inbound_messages_content_digest_format", "content_sha256 IS NULL OR content_sha256 ~ '^[0-9a-f]{64}$'");
            t.HasCheckConstraint(
                "inbound_messages_parse_consistent",
                $"(latest_parse_run_id IS NULL AND latest_parse_outcome IS NULL AND parsed_at IS NULL) "
                + $"OR (latest_parse_run_id IS NOT NULL AND latest_parse_outcome IN ({SnakeCaseEnum<ParseOutcome>.SqlList}) AND parsed_at IS NOT NULL)");
        });

        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();
        builder.Property(m => m.ProviderInstanceId).HasMaxLength(50);
        builder.Property(m => m.SourceMessageId).HasMaxLength(200);
        builder.Property(m => m.SourceKind).HasConversion<LowercaseEnumConverter<InboundSourceKind>>().HasMaxLength(20);
        builder.Property(m => m.Environment).HasConversion<LowercaseEnumConverter<InboundEnvironment>>().HasMaxLength(10);
        builder.Property(m => m.RoutingState).HasConversion<SnakeCaseEnumConverter<InboundRoutingState>>().HasMaxLength(40);
        builder.Property(m => m.HoldReason).HasConversion<SnakeCaseEnumConverter<InboundHoldReason>>().HasMaxLength(30);
        builder.Property(m => m.EnvelopeRecipients).HasColumnType("text[]");
        builder.Property(m => m.RecipientEvidence).HasMaxLength(100);
        builder.Property(m => m.EnvelopeSender).HasMaxLength(1000);
        builder.Property(m => m.HeaderFrom).HasMaxLength(1000);
        builder.Property(m => m.Subject).HasMaxLength(2000);
        builder.Property(m => m.TransportKind).HasMaxLength(50);
        builder.Property(m => m.DeliveryEvidence).HasMaxLength(100);
        builder.Property(m => m.ContentMode).HasConversion<LowercaseEnumConverter<InboundContentMode>>().HasMaxLength(20);
        builder.Property(m => m.ContentReference).HasMaxLength(1000);
        builder.Property(m => m.ContentState).HasConversion<LowercaseEnumConverter<InboundContentState>>().HasMaxLength(20);
        builder.Property(m => m.ContentSha256).HasMaxLength(64);
        builder.Property(m => m.ContentRecipients).HasColumnType("text[]");
        builder.Property(m => m.SpfResult).HasMaxLength(20);
        builder.Property(m => m.DkimResult).HasMaxLength(20);
        builder.Property(m => m.DmarcResult).HasMaxLength(20);
        builder.Property(m => m.AuthenticationOrigin).HasMaxLength(100);
        builder.Property(m => m.FetchErrorCode).HasMaxLength(50);
        builder.Property(m => m.FetchErrorMessage).HasMaxLength(500);
        builder.Property(m => m.LatestParseOutcome).HasConversion<SnakeCaseEnumConverter<ParseOutcome>>().HasMaxLength(30);

        // Feature 007's projection of the latest completed run; the run itself references the message with its owner.
        builder.HasOne<MessageParseRun>().WithMany().HasForeignKey(m => m.LatestParseRunId).OnDelete(DeleteBehavior.Restrict);

        // JSON fields are stored as text exactly as supplied: jsonb would reorder them and rejects NUL characters.

        // Operator routing retries and content completions never overwrite each other unseen.
        builder.Property<uint>("xmin").IsRowVersion();

        builder.HasOne<Tenant>().WithMany().HasForeignKey(m => m.TenantId).OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<BankConnection>()
            .WithMany()
            .HasForeignKey(m => new { m.TenantId, m.BankConnectionId })
            .HasPrincipalKey(b => new { b.TenantId, b.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // The address, its tenant, and its connection must all match the message's owner.
        builder.HasOne<InboundAddress>()
            .WithMany()
            .HasForeignKey(m => new { m.InboundAddressId, m.TenantId, m.BankConnectionId })
            .HasPrincipalKey(a => new { a.Id, a.TenantId, a.BankConnectionId })
            .OnDelete(DeleteBehavior.Restrict);

        // Source IDs are unique only within their instance; another account may use the same ID.
        builder.HasIndex(m => new { m.ProviderInstanceId, m.SourceMessageId }).IsUnique().HasDatabaseName(SourceMessageUniqueIndex);

        // IDs are UUIDv7, so (tenant_id, id) also serves listing by acceptance time.
        builder.HasIndex(m => new { m.TenantId, m.Id }).HasDatabaseName("inbound_messages_tenant_idx");
        builder.HasIndex(m => new { m.BankConnectionId, m.ReceivedAt }).HasDatabaseName("inbound_messages_connection_idx");
        builder.HasIndex(m => m.RoutingState)
            .HasFilter("routing_state <> 'routed'")
            .HasDatabaseName("inbound_messages_quarantine_idx");
    }
}

internal sealed class BackgroundJobConfiguration : IEntityTypeConfiguration<BackgroundJob>
{
    public const string OneActiveIndex = "background_jobs_one_active";
    public const string GenerationUniqueIndex = "background_jobs_generation_unique";

    public void Configure(EntityTypeBuilder<BackgroundJob> builder)
    {
        builder.ToTable("background_jobs", t =>
        {
            t.HasCheckConstraint("background_jobs_state_valid", $"state IN ({SnakeCaseEnum<BackgroundJobState>.SqlList})");
            t.HasCheckConstraint("background_jobs_counts_valid", "generation >= 1 AND attempt_count >= 0");
            t.HasCheckConstraint(
                "background_jobs_lease_consistent",
                "(state = 'running' AND lease_token IS NOT NULL AND lease_expires_at IS NOT NULL AND lease_owner IS NOT NULL) "
                + "OR (state <> 'running' AND lease_token IS NULL AND lease_expires_at IS NULL AND lease_owner IS NULL)");
            t.HasCheckConstraint(
                "background_jobs_completion_consistent",
                "(state IN ('succeeded', 'failed') AND completed_at IS NOT NULL) OR (state NOT IN ('succeeded', 'failed') AND completed_at IS NULL)");
        });

        builder.HasKey(j => j.Id);
        builder.Property(j => j.Id).ValueGeneratedNever();
        builder.Property(j => j.Type).HasMaxLength(50);
        builder.Property(j => j.State).HasConversion<SnakeCaseEnumConverter<BackgroundJobState>>().HasMaxLength(20);
        builder.Property(j => j.LeaseOwner).HasMaxLength(200);
        builder.Property(j => j.LastErrorCode).HasMaxLength(50);
        builder.Property(j => j.LastErrorMessage).HasMaxLength(500);
        builder.Property(j => j.RequestedBy).HasMaxLength(200);

        builder.HasIndex(j => new { j.Type, j.TargetId })
            .IsUnique()
            .HasFilter("state IN ('queued', 'running', 'retry_wait')")
            .HasDatabaseName(OneActiveIndex);
        builder.HasIndex(j => new { j.Type, j.TargetId, j.Generation }).IsUnique().HasDatabaseName(GenerationUniqueIndex);
        builder.HasIndex(j => j.DueAt)
            .HasFilter("state IN ('queued', 'running', 'retry_wait')")
            .HasDatabaseName("background_jobs_due_idx");
    }
}

internal sealed class InboundSyncStateConfiguration : IEntityTypeConfiguration<InboundSyncState>
{
    public const string InstanceUniqueIndex = "inbound_sync_states_instance_unique";

    public void Configure(EntityTypeBuilder<InboundSyncState> builder)
    {
        builder.ToTable("inbound_sync_states", t =>
        {
            t.HasCheckConstraint("inbound_sync_states_counts_valid", "discovered_count >= 0 AND last_run_discovered_count >= 0");
            t.HasCheckConstraint("inbound_sync_states_coverage_valid", "covered_through IS NULL OR covered_through >= sync_from");
        });

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.ProviderInstanceId).HasMaxLength(50);
        builder.Property(s => s.LastErrorCode).HasMaxLength(50);
        builder.Property(s => s.LastErrorMessage).HasMaxLength(500);
        builder.Property(s => s.CoverageGap).HasMaxLength(500);

        // Boundary updates are serialized by the discovery job's lease; the row version guards operator edits.
        builder.Property<uint>("xmin").IsRowVersion();

        builder.HasIndex(s => s.ProviderInstanceId).IsUnique().HasDatabaseName(InstanceUniqueIndex);
    }
}
