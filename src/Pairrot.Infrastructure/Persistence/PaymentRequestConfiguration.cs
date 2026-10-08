using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pairrot.Core.Banking;
using Pairrot.Core.Payments;
using Pairrot.Core.Tenants;

namespace Pairrot.Infrastructure.Persistence;

// Feature 005: expected payments with frozen payment instructions.

internal sealed class PaymentRequestConfiguration : IEntityTypeConfiguration<PaymentRequest>
{
    public const string ExternalIdUniqueIndex = "payment_requests_tenant_external_id_unique";
    public const string VariableSymbolUniqueIndex = "payment_requests_account_variable_symbol_unique";

    /// <summary>The symbol key as a number, for the allocator's indexed gap search (Feature 012); computed by the database.</summary>
    public const string VariableSymbolNumberProperty = "VariableSymbolNumber";

    public void Configure(EntityTypeBuilder<PaymentRequest> builder)
    {
        builder.ToTable("payment_requests", t =>
        {
            t.HasCheckConstraint("payment_requests_external_id_not_blank", "btrim(external_id) <> ''");
            t.HasCheckConstraint("payment_requests_amount_range",
                $"amount BETWEEN {SpaydPayment.MinAmount} AND {SpaydPayment.MaxAmount}");
            t.HasCheckConstraint("payment_requests_currency_valid", "currency = 'CZK'");
            t.HasCheckConstraint(
                "payment_requests_iban_format",
                $"iban ~ '^CZ[0-9]{{22}}$' OR (erased_at IS NOT NULL AND iban ~ '{ErasureRules.MaskedIbanPattern}')");
            t.HasCheckConstraint(
                "payment_requests_erasure_consistent",
                $"(erased_at IS NULL AND qr_payload IS NOT NULL) OR (erased_at IS NOT NULL AND qr_payload IS NULL "
                + $"AND message IS NULL AND description IS NULL AND cancellation_reason IS NULL AND external_id LIKE '{ErasureRules.TombstonePrefix}%')");
            t.HasCheckConstraint("payment_requests_variable_symbol_format", "variable_symbol ~ '^[0-9]{1,10}$'");
            t.HasCheckConstraint("payment_requests_variable_symbol_key_format", "variable_symbol_key ~ '^(0|[1-9][0-9]{0,9})$'");
            t.HasCheckConstraint("payment_requests_status_valid", $"status IN ({SnakeCaseEnum<PaymentRequestStatus>.SqlList})");
            t.HasCheckConstraint("payment_requests_variable_symbol_source_valid",
                $"variable_symbol_source IN ({SnakeCaseEnum<VariableSymbolSource>.SqlList})");

            // Feature 009: the stored status follows from the paid amount; a cancelled request has nothing paid.
            t.HasCheckConstraint(
                "payment_requests_paid_consistent",
                "(status = 'open' AND paid_amount = 0) "
                + "OR (status = 'partially_paid' AND paid_amount > 0 AND paid_amount < amount) "
                + "OR (status = 'paid' AND paid_amount = amount) "
                + "OR (status = 'overpaid' AND paid_amount > amount) "
                + "OR (status = 'cancelled' AND paid_amount = 0)");
            t.HasCheckConstraint("payment_requests_change_sequence_valid", "change_sequence >= 0");
            t.HasCheckConstraint(
                "payment_requests_cancellation_consistent",
                "(status = 'cancelled' AND cancelled_at IS NOT NULL) "
                + "OR (status <> 'cancelled' AND cancelled_at IS NULL AND cancellation_reason IS NULL)");
            t.HasCheckConstraint("payment_requests_fingerprint_format", "creation_fingerprint ~ '^[0-9a-f]{64}$'");
            t.HasCheckConstraint("payment_requests_qr_format_valid", "qr_format = 'SPAYD'");
            t.HasCheckConstraint("payment_requests_version_positive", "version >= 1");
        });

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.ExternalId).HasMaxLength(PaymentRequestRules.MaxExternalIdLength);
        builder.Property(p => p.AccountNumber).HasMaxLength(30);
        builder.Property(p => p.Iban).HasMaxLength(34);
        builder.Property(p => p.Currency).HasMaxLength(3);
        builder.Property(p => p.VariableSymbol).HasMaxLength(SpaydPayment.MaxVariableSymbolLength);
        builder.Property(p => p.VariableSymbolKey).HasMaxLength(SpaydPayment.MaxVariableSymbolLength);
        builder.Property(p => p.VariableSymbolSource)
            .HasConversion<SnakeCaseEnumConverter<VariableSymbolSource>>()
            .HasMaxLength(20);
        builder.Property<long>(VariableSymbolNumberProperty)
            .HasComputedColumnSql("(variable_symbol_key::bigint)", stored: true);
        // Characters, not bytes; the payload limit is enforced when the message is validated.
        builder.Property(p => p.Message).HasMaxLength(SpaydPayment.MaxMessageLength * 2);
        builder.Property(p => p.Description).HasMaxLength(PaymentRequestRules.MaxDescriptionLength);
        builder.Property(p => p.Status)
            .HasConversion<SnakeCaseEnumConverter<PaymentRequestStatus>>()
            .HasMaxLength(20);
        builder.Property(p => p.CancellationReason).HasMaxLength(PaymentRequestRules.MaxCancellationReasonLength);
        builder.Property(p => p.CreationFingerprint).HasMaxLength(64);
        builder.Property(p => p.QrPayload).HasMaxLength(1000);
        builder.Property(p => p.QrFormat).HasMaxLength(20);
        builder.Property(p => p.QrGeneratorVersion).HasMaxLength(50);

        // Of two updates based on the same version, only the first is saved.
        builder.Property(p => p.Version).IsConcurrencyToken();

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(p => p.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        // The connection must belong to the same tenant and the stated account, even if application code got it wrong.
        builder.HasOne<BankConnection>()
            .WithMany()
            .HasForeignKey(p => new { p.TenantId, p.BankConnectionId, p.BankAccountId })
            .HasPrincipalKey(b => new { b.TenantId, b.Id, b.BankAccountId })
            .OnDelete(DeleteBehavior.Restrict);

        // Lets allocations and changes reference a request together with its owner and connection.
        builder.HasAlternateKey(p => new { p.Id, p.TenantId, p.BankConnectionId });

        builder.HasIndex(p => new { p.TenantId, p.ExternalId }).IsUnique().HasDatabaseName(ExternalIdUniqueIndex);

        // Cancelled requests keep their symbol: a late payment must still be attributable. Feature
        // 013: unique per physical account, over every registration of it in any tenant.
        builder.HasIndex(p => new { p.BankAccountId, p.VariableSymbolKey }).IsUnique().HasDatabaseName(VariableSymbolUniqueIndex);
        builder.HasIndex(nameof(PaymentRequest.BankAccountId), VariableSymbolNumberProperty)
            .HasDatabaseName("payment_requests_account_variable_symbol_number_idx");
        builder.HasIndex(p => new { p.BankConnectionId, p.Id }).HasDatabaseName("payment_requests_connection_idx");

        // IDs are UUIDv7, so (tenant_id, id) also serves listing by creation time.
        builder.HasIndex(p => new { p.TenantId, p.Id }).HasDatabaseName("payment_requests_tenant_idx");
        builder.HasIndex(p => new { p.TenantId, p.Status, p.Id }).HasDatabaseName("payment_requests_tenant_status_idx");
        builder.HasIndex(p => new { p.TenantId, p.Status, p.DueAt }).HasDatabaseName("payment_requests_tenant_due_idx");
    }
}

// Feature 012: automatic variable symbols; Feature 013: one allocator per physical account.

internal sealed class VariableSymbolAllocatorConfiguration : IEntityTypeConfiguration<VariableSymbolAllocator>
{
    public void Configure(EntityTypeBuilder<VariableSymbolAllocator> builder)
    {
        builder.ToTable("variable_symbol_allocators", t => t.HasCheckConstraint(
            "variable_symbol_allocators_next_candidate_range",
            $"next_candidate BETWEEN 1 AND {PaymentRequestRules.ExhaustedVariableSymbolCursor}"));

        builder.HasKey(a => a.BankAccountId);
        builder.Property(a => a.BankAccountId).ValueGeneratedNever();

        builder.HasOne<BankAccount>()
            .WithOne()
            .HasForeignKey<VariableSymbolAllocator>(a => a.BankAccountId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
