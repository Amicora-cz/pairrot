using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pairrot.Core.Banking;
using Pairrot.Core.Matching;
using Pairrot.Core.Payments;
using Pairrot.Core.Tenants;
using Pairrot.Core.Transactions;

namespace Pairrot.Infrastructure.Persistence;

// Feature 009: allocations, payment changes, review cases, and manual command receipts. Both
// sides of every link carry the same tenant and, since Feature 013, the same bank account; the
// connections of one link may differ when both register that account. Enforced by composite keys.

internal sealed class PaymentAllocationConfiguration : IEntityTypeConfiguration<PaymentAllocation>
{
    public const string OneActiveIndex = "payment_allocations_one_active_per_transaction";

    public void Configure(EntityTypeBuilder<PaymentAllocation> builder)
    {
        builder.ToTable("payment_allocations", t =>
        {
            t.HasCheckConstraint("payment_allocations_amount_positive", "amount > 0");
            t.HasCheckConstraint("payment_allocations_currency_valid", "currency ~ '^[A-Z]{3}$'");
            t.HasCheckConstraint("payment_allocations_method_valid", $"method IN ({SnakeCaseEnum<AllocationMethod>.SqlList})");
            t.HasCheckConstraint("payment_allocations_state_valid", $"state IN ({SnakeCaseEnum<AllocationState>.SqlList})");
            t.HasCheckConstraint("payment_allocations_evidence_valid", "evidence IN ('live', 'test', 'fixture')");
            t.HasCheckConstraint("payment_allocations_manual_reason", "method = 'automatic' OR btrim(coalesce(reason, '')) <> ''");
            t.HasCheckConstraint(
                "payment_allocations_revocation_consistent",
                "(state = 'active' AND revoked_at IS NULL AND revoked_by IS NULL AND revocation_code IS NULL) "
                + "OR (state = 'revoked' AND revoked_at IS NOT NULL AND revoked_by IS NOT NULL AND revocation_code IS NOT NULL)");
            t.HasCheckConstraint("payment_allocations_version_positive", "version >= 1");
        });

        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.Currency).HasMaxLength(3);
        builder.Property(a => a.Method).HasConversion<SnakeCaseEnumConverter<AllocationMethod>>().HasMaxLength(20);
        builder.Property(a => a.State).HasConversion<SnakeCaseEnumConverter<AllocationState>>().HasMaxLength(20);
        builder.Property(a => a.Reason).HasMaxLength(MatchingRules.MaxReasonLength);
        builder.Property(a => a.Evidence).HasMaxLength(10);
        builder.Property(a => a.CreatedBy).HasMaxLength(200);
        builder.Property(a => a.RevokedBy).HasMaxLength(200);
        builder.Property(a => a.RevocationCode).HasMaxLength(50);
        builder.Property(a => a.RevocationReason).HasMaxLength(MatchingRules.MaxReasonLength);
        builder.Property(a => a.Version).IsConcurrencyToken();

        builder.HasOne<IncomingTransaction>()
            .WithMany()
            .HasForeignKey(a => new { a.TransactionId, a.TenantId, a.BankConnectionId })
            .HasPrincipalKey(t => new { t.Id, t.TenantId, t.BankConnectionId })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<PaymentRequest>()
            .WithMany()
            .HasForeignKey(a => new { a.PaymentRequestId, a.TenantId, a.PaymentRequestBankConnectionId })
            .HasPrincipalKey(p => new { p.Id, p.TenantId, p.BankConnectionId })
            .OnDelete(DeleteBehavior.Restrict);

        // Both connections belong to the tenant and register the one stated account: an allocation
        // across accounts or tenants is rejected by the database as well as by the service.
        builder.HasOne<BankConnection>()
            .WithMany()
            .HasForeignKey(a => new { a.TenantId, a.BankConnectionId, a.BankAccountId })
            .HasPrincipalKey(b => new { b.TenantId, b.Id, b.BankAccountId })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<BankConnection>()
            .WithMany()
            .HasForeignKey(a => new { a.TenantId, a.PaymentRequestBankConnectionId, a.BankAccountId })
            .HasPrincipalKey(b => new { b.TenantId, b.Id, b.BankAccountId })
            .OnDelete(DeleteBehavior.Restrict);

        // A receipt counts at most once.
        builder.HasIndex(a => a.TransactionId)
            .IsUnique()
            .HasFilter("state = 'active'")
            .HasDatabaseName(OneActiveIndex);
        builder.HasIndex(a => new { a.PaymentRequestId, a.Id }).HasDatabaseName("payment_allocations_request_idx");
    }
}

internal sealed class PaymentChangeConfiguration : IEntityTypeConfiguration<PaymentChange>
{
    public const string SequenceUniqueIndex = "payment_request_changes_sequence_unique";

    public void Configure(EntityTypeBuilder<PaymentChange> builder)
    {
        var statuses = SnakeCaseEnum<PaymentRequestStatus>.SqlList;
        builder.ToTable("payment_request_changes", t =>
        {
            t.HasCheckConstraint("payment_request_changes_sequence_positive", "sequence >= 1");
            t.HasCheckConstraint("payment_request_changes_status_before_valid", $"status_before IN ({statuses})");
            t.HasCheckConstraint("payment_request_changes_status_after_valid", $"status_after IN ({statuses})");
            t.HasCheckConstraint("payment_request_changes_cause_valid", $"cause IN ({SnakeCaseEnum<PaymentChangeCause>.SqlList})");
            t.HasCheckConstraint("payment_request_changes_amounts_valid", "paid_before >= 0 AND paid_after >= 0 AND overpaid_before >= 0 AND overpaid_after >= 0");
        });

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.ExternalId).HasMaxLength(PaymentRequestRules.MaxExternalIdLength);
        builder.Property(c => c.Currency).HasMaxLength(3);
        builder.Property(c => c.StatusBefore).HasConversion<SnakeCaseEnumConverter<PaymentRequestStatus>>().HasMaxLength(20);
        builder.Property(c => c.StatusAfter).HasConversion<SnakeCaseEnumConverter<PaymentRequestStatus>>().HasMaxLength(20);
        builder.Property(c => c.Cause).HasConversion<SnakeCaseEnumConverter<PaymentChangeCause>>().HasMaxLength(40);
        builder.Property(c => c.Evidence).HasMaxLength(10);
        builder.Property(c => c.Actor).HasMaxLength(200);

        builder.HasOne<PaymentRequest>()
            .WithMany()
            .HasForeignKey(c => new { c.PaymentRequestId, c.TenantId, c.BankConnectionId })
            .HasPrincipalKey(p => new { p.Id, p.TenantId, p.BankConnectionId })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<BankConnection>()
            .WithMany()
            .HasForeignKey(c => new { c.TenantId, c.BankConnectionId, c.BankAccountId })
            .HasPrincipalKey(b => new { b.TenantId, b.Id, b.BankAccountId })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<PaymentAllocation>()
            .WithMany()
            .HasForeignKey(c => c.AllocationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(c => new { c.PaymentRequestId, c.Sequence }).IsUnique().HasDatabaseName(SequenceUniqueIndex);
        builder.HasIndex(c => new { c.TenantId, c.Id }).HasDatabaseName("payment_request_changes_tenant_idx");
    }
}

internal sealed class PaymentReviewCaseConfiguration : IEntityTypeConfiguration<PaymentReviewCase>
{
    public const string MessageUniqueIndex = "payment_review_cases_message_unique";

    public void Configure(EntityTypeBuilder<PaymentReviewCase> builder)
    {
        builder.ToTable("payment_review_cases", t =>
        {
            t.HasCheckConstraint("payment_review_cases_issue_valid", $"issue IN ({SnakeCaseEnum<ReviewIssue>.SqlList})");
            t.HasCheckConstraint("payment_review_cases_state_valid", $"state IN ({SnakeCaseEnum<ReviewCaseState>.SqlList})");
            t.HasCheckConstraint(
                "payment_review_cases_resolution_valid",
                $"resolution IS NULL OR resolution IN ({SnakeCaseEnum<ReviewAction>.SqlList})");
            t.HasCheckConstraint(
                "payment_review_cases_message_consistent",
                "(issue = 'unverified_candidate') = (inbound_message_id IS NOT NULL)");
            t.HasCheckConstraint(
                "payment_review_cases_resolved_consistent",
                "(state = 'resolved' AND resolved_at IS NOT NULL AND resolution IS NOT NULL) OR state = 'open'");
            t.HasCheckConstraint("payment_review_cases_version_positive", "version >= 1");
        });

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.Issue).HasConversion<SnakeCaseEnumConverter<ReviewIssue>>().HasMaxLength(30);
        builder.Property(c => c.State).HasConversion<SnakeCaseEnumConverter<ReviewCaseState>>().HasMaxLength(20);
        builder.Property(c => c.Resolution).HasConversion<SnakeCaseEnumConverter<ReviewAction>>().HasMaxLength(30);
        builder.Property(c => c.ReasonCode).HasMaxLength(50);
        builder.Property(c => c.ReasonMessage).HasMaxLength(500);
        builder.Property(c => c.EvidenceKey).HasMaxLength(1000);
        builder.Property(c => c.Version).IsConcurrencyToken();

        builder.HasOne<BankConnection>()
            .WithMany()
            .HasForeignKey(c => new { c.TenantId, c.BankConnectionId, c.BankAccountId })
            .HasPrincipalKey(b => new { b.TenantId, b.Id, b.BankAccountId })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasAlternateKey(c => new { c.Id, c.TenantId, c.BankConnectionId, c.BankAccountId });

        // One case per reviewed message; a new question about it reopens that case.
        builder.HasIndex(c => c.InboundMessageId)
            .IsUnique()
            .HasFilter("inbound_message_id IS NOT NULL")
            .HasDatabaseName(MessageUniqueIndex);
        builder.HasIndex(c => new { c.TenantId, c.State, c.Id }).HasDatabaseName("payment_review_cases_tenant_state_idx");
    }
}

internal sealed class PaymentReviewCaseMemberConfiguration : IEntityTypeConfiguration<PaymentReviewCaseMember>
{
    public void Configure(EntityTypeBuilder<PaymentReviewCaseMember> builder)
    {
        builder.ToTable("payment_review_case_members", t =>
            t.HasCheckConstraint("payment_review_case_members_one_target", "(transaction_id IS NULL) <> (parse_run_id IS NULL)"));

        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();

        builder.HasOne<PaymentReviewCase>()
            .WithMany()
            .HasForeignKey(m => new { m.CaseId, m.TenantId, m.BankConnectionId, m.BankAccountId })
            .HasPrincipalKey(c => new { c.Id, c.TenantId, c.BankConnectionId, c.BankAccountId })
            .OnDelete(DeleteBehavior.Restrict);

        // Members of one tenant may come from any registration of the case's account.
        builder.HasOne<IncomingTransaction>()
            .WithMany()
            .HasForeignKey(m => new { m.TransactionId, m.TenantId, m.BankAccountId })
            .HasPrincipalKey(t => new { t.Id, t.TenantId, t.BankAccountId })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<MessageParseRun>()
            .WithMany()
            .HasForeignKey(m => new { m.ParseRunId, m.TenantId, m.BankAccountId })
            .HasPrincipalKey(r => new { r.Id, r.TenantId, r.BankAccountId })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(m => new { m.CaseId, m.TransactionId })
            .IsUnique()
            .HasFilter("transaction_id IS NOT NULL")
            .HasDatabaseName("payment_review_case_members_transaction_unique");
        builder.HasIndex(m => new { m.CaseId, m.ParseRunId })
            .IsUnique()
            .HasFilter("parse_run_id IS NOT NULL")
            .HasDatabaseName("payment_review_case_members_run_unique");
        builder.HasIndex(m => m.TransactionId).HasDatabaseName("payment_review_case_members_transaction_idx");
    }
}

internal sealed class PaymentReviewDecisionConfiguration : IEntityTypeConfiguration<PaymentReviewDecision>
{
    public void Configure(EntityTypeBuilder<PaymentReviewDecision> builder)
    {
        builder.ToTable("payment_review_decisions", t =>
        {
            t.HasCheckConstraint("payment_review_decisions_action_valid", $"action IN ({SnakeCaseEnum<ReviewAction>.SqlList})");
            t.HasCheckConstraint("payment_review_decisions_reason_not_blank", "btrim(reason) <> ''");
        });

        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedNever();
        builder.Property(d => d.Action).HasConversion<SnakeCaseEnumConverter<ReviewAction>>().HasMaxLength(30);
        builder.Property(d => d.Reason).HasMaxLength(MatchingRules.MaxReasonLength);
        builder.Property(d => d.ReasonCode).HasMaxLength(50);
        builder.Property(d => d.Actor).HasMaxLength(200);

        builder.HasOne<PaymentReviewCase>()
            .WithMany()
            .HasForeignKey(d => d.CaseId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(d => new { d.CaseId, d.CreatedAt }).HasDatabaseName("payment_review_decisions_case_idx");
    }
}

internal sealed class ManualCommandReceiptConfiguration : IEntityTypeConfiguration<ManualCommandReceipt>
{
    public const string KeyUniqueIndex = "manual_command_receipts_key_unique";

    public void Configure(EntityTypeBuilder<ManualCommandReceipt> builder)
    {
        builder.ToTable("manual_command_receipts", t =>
            t.HasCheckConstraint("manual_command_receipts_fingerprint_format", "fingerprint ~ '^[0-9a-f]{64}$'"));

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.IdempotencyKey).HasMaxLength(200);
        builder.Property(r => r.Action).HasMaxLength(50);
        builder.Property(r => r.Fingerprint).HasMaxLength(64);
        builder.Property(r => r.Actor).HasMaxLength(200);

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(r => r.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(r => new { r.TenantId, r.IdempotencyKey }).IsUnique().HasDatabaseName(KeyUniqueIndex);
    }
}
