using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pairrot.Core.Banking;
using Pairrot.Core.Tenants;

namespace Pairrot.Infrastructure.Persistence;

// Feature 003: one row per registration of a bank account by a tenant. Its addresses are in
// inbound_addresses (Feature 006). Feature 013: the physical account is bank_accounts, and only
// active registrations are exclusive.

internal sealed class BankAccountConfiguration : IEntityTypeConfiguration<BankAccount>
{
    public const string IbanUniqueIndex = "bank_accounts_iban_unique";

    public void Configure(EntityTypeBuilder<BankAccount> builder)
    {
        builder.ToTable("bank_accounts", t =>
        {
            t.HasCheckConstraint("bank_accounts_iban_format", "iban ~ '^CZ[0-9]{22}$'");
            t.HasCheckConstraint("bank_accounts_account_number_not_blank", "btrim(account_number) <> ''");
            t.HasCheckConstraint("bank_accounts_bank_code_format", "bank_code ~ '^[0-9]{4}$'");
            t.HasCheckConstraint("bank_accounts_currency_valid", "currency = 'CZK'");
        });

        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.Iban).HasMaxLength(34);
        builder.Property(a => a.AccountNumber).HasMaxLength(30);
        builder.Property(a => a.BankCode).HasMaxLength(4);
        builder.Property(a => a.Currency).HasMaxLength(3);

        // One row per physical account, kept forever.
        builder.HasIndex(a => a.Iban).IsUnique().HasDatabaseName(IbanUniqueIndex);
    }
}

internal sealed class BankConnectionConfiguration : IEntityTypeConfiguration<BankConnection>
{
    /// <summary>At most one active registration per account in the whole system.</summary>
    public const string OneActivePerAccountIndex = "bank_connections_one_active_per_account";

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
                "bank_connections_archived_reason_valid",
                $"archived_reason IS NULL OR archived_reason IN ({SnakeCaseEnum<BankConnectionArchivedReason>.SqlList})");
            t.HasCheckConstraint(
                "bank_connections_archive_consistent",
                "(status = 'archived' AND archived_at IS NOT NULL AND archived_reason IS NOT NULL) "
                + "OR (status <> 'archived' AND archived_at IS NULL AND archived_reason IS NULL)");
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
        builder.Property(b => b.ArchivedReason)
            .HasConversion<SnakeCaseEnumConverter<BankConnectionArchivedReason>>()
            .HasMaxLength(20);

        // Optimistic concurrency: an address assignment and a concurrent archive cannot both be saved.
        builder.Property<uint>("xmin").IsRowVersion();

        // Deleting a tenant is a status change; its connections stay for history.
        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(b => b.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<BankAccount>()
            .WithMany()
            .HasForeignKey(b => b.BankAccountId)
            .OnDelete(DeleteBehavior.Restrict);

        // Lets every record of a connection state its account, so records of one tenant and one
        // account can reference each other across that account's connections (Feature 013).
        builder.HasAlternateKey(b => new { b.TenantId, b.Id, b.BankAccountId });

        builder.HasIndex(b => b.BankAccountId)
            .IsUnique()
            .HasFilter("status = 'active'")
            .HasDatabaseName(OneActivePerAccountIndex);

        builder.HasIndex(b => b.Iban).HasDatabaseName("bank_connections_iban_idx");
        builder.HasIndex(b => new { b.TenantId, b.Status, b.Id }).HasDatabaseName("bank_connections_tenant_idx");
    }
}
