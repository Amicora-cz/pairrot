using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pairrot.Core.Banking;
using Pairrot.Core.Tenants;

namespace Pairrot.Infrastructure.Persistence;

// Feature 003: one row per bank account a tenant uses, with its notification address.

internal sealed class BankConnectionConfiguration : IEntityTypeConfiguration<BankConnection>
{
    public const string IbanUniqueIndex = "bank_connections_iban_unique";
    public const string AddressUniqueIndex = "bank_connections_inbound_address_unique";

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
            t.HasCheckConstraint("bank_connections_address_status_valid", "address_status IN ('not_created', 'stub_created')");
            t.HasCheckConstraint(
                "bank_connections_address_consistent",
                "(address_status = 'not_created' AND inbound_address IS NULL AND address_created_at IS NULL AND email_provider IS NULL) "
                + "OR (address_status = 'stub_created' AND inbound_address IS NOT NULL AND address_created_at IS NOT NULL "
                + "AND email_provider = 'stub')");
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
        builder.Property(b => b.EmailProvider).HasMaxLength(20);
        builder.Property(b => b.InboundAddress).HasMaxLength(TenantRules.MaxEmailLength);
        builder.Property(b => b.AddressStatus)
            .HasConversion(new ValueConverter<AddressStatus, string>(
                value => value == AddressStatus.StubCreated ? "stub_created" : "not_created",
                value => value == "stub_created" ? AddressStatus.StubCreated : AddressStatus.NotCreated))
            .HasMaxLength(20);

        // Optimistic concurrency: of two simultaneous address assignments, only one is saved.
        builder.Property<uint>("xmin").IsRowVersion();

        // Deleting a tenant is a status change; its connections stay for history.
        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(b => b.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        // Archived connections keep their account reserved: no reuse, in any tenant.
        builder.HasIndex(b => b.Iban).IsUnique().HasDatabaseName(IbanUniqueIndex);

        // PostgreSQL treats NULLs as distinct, so connections without an address do not conflict.
        builder.HasIndex(b => b.InboundAddress).IsUnique().HasDatabaseName(AddressUniqueIndex);

        builder.HasIndex(b => new { b.TenantId, b.Status, b.Id }).HasDatabaseName("bank_connections_tenant_idx");
    }
}
