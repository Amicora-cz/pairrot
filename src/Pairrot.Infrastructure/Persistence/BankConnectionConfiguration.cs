using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pairrot.Core.Banking;
using Pairrot.Core.Tenants;

namespace Pairrot.Infrastructure.Persistence;

// Feature 003: one row per bank account a tenant uses. Its addresses are in inbound_addresses (Feature 006).

internal sealed class BankConnectionConfiguration : IEntityTypeConfiguration<BankConnection>
{
    public const string IbanUniqueIndex = "bank_connections_iban_unique";

    public void Configure(EntityTypeBuilder<BankConnection> builder)
    {
        builder.ToTable("bank_connections", t =>
        {
            t.HasCheckConstraint("bank_connections_iban_format", "iban ~ '^CZ[0-9]{22}$'");
            t.HasCheckConstraint("bank_connections_account_number_not_blank", "btrim(account_number) <> ''");
            t.HasCheckConstraint("bank_connections_currency_valid", "currency = 'CZK'");
            t.HasCheckConstraint("bank_connections_label_not_blank", "label IS NULL OR btrim(label) <> ''");
            t.HasCheckConstraint("bank_connections_status_valid", "status IN ('active', 'archived')");
            t.HasCheckConstraint(
                "bank_connections_archive_consistent",
                "(status = 'archived' AND archived_at IS NOT NULL) OR (status <> 'archived' AND archived_at IS NULL)");
        });

        builder.HasKey(b => b.Id);
        builder.Property(b => b.Id).ValueGeneratedNever();
        builder.Property(b => b.Label).HasMaxLength(TenantRules.MaxLabelLength);
        builder.Property(b => b.AccountNumber).HasMaxLength(30);
        builder.Property(b => b.Iban).HasMaxLength(34);
        builder.Property(b => b.Currency).HasMaxLength(3);
        builder.Property(b => b.Status)
            .HasConversion<LowercaseEnumConverter<BankConnectionStatus>>()
            .HasMaxLength(20);

        // Optimistic concurrency: an address assignment and a concurrent archive cannot both be saved.
        builder.Property<uint>("xmin").IsRowVersion();

        // Deleting a tenant is a status change; its connections stay for history.
        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(b => b.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        // Archived connections keep their account reserved: no reuse, in any tenant.
        builder.HasIndex(b => b.Iban).IsUnique().HasDatabaseName(IbanUniqueIndex);

        builder.HasIndex(b => new { b.TenantId, b.Status, b.Id }).HasDatabaseName("bank_connections_tenant_idx");
    }
}
