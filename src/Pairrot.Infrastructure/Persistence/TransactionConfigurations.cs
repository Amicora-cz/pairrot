using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pairrot.Core.Banking;
using Pairrot.Core.Inbound;
using Pairrot.Core.Parsing;
using Pairrot.Core.Transactions;

namespace Pairrot.Infrastructure.Persistence;

// Feature 007: parse runs, incoming transactions, and the messages supporting them. Every row
// carries its tenant, connection, and (Feature 013) account; the connection reference includes
// all three. References between records include the tenant and the account, so no run,
// transaction, or link can join records of different owners or accounts, while records of one
// tenant may link across that account's registrations. References to inbound_messages, whose
// owner columns are nullable, are composite foreign keys added in the migration's SQL.

internal sealed class MessageParseRunConfiguration : IEntityTypeConfiguration<MessageParseRun>
{
    public const string GenerationUniqueIndex = "message_parse_runs_message_generation_unique";

    public void Configure(EntityTypeBuilder<MessageParseRun> builder)
    {
        builder.ToTable("message_parse_runs", t =>
        {
            t.HasCheckConstraint("message_parse_runs_outcome_valid", $"outcome IN ({SnakeCaseEnum<ParseOutcome>.SqlList})");
            t.HasCheckConstraint(
                "message_parse_runs_trust_valid",
                $"trust_decision IS NULL OR trust_decision IN ({SnakeCaseEnum<TrustDecision>.SqlList})");
            t.HasCheckConstraint("message_parse_runs_part_valid", "source_part IS NULL OR source_part IN ('text', 'html')");
            t.HasCheckConstraint("message_parse_runs_generation_valid", "job_generation >= 1");
            t.HasCheckConstraint("message_parse_runs_digest_format", "source_sha256 ~ '^[0-9a-f]{64}$'");
            t.HasCheckConstraint("message_parse_runs_times_valid", "completed_at >= started_at");

            // Only parsed and duplicate runs establish a link; a review run may name the transaction it disputes.
            t.HasCheckConstraint(
                "message_parse_runs_transaction_consistent",
                "outcome NOT IN ('parsed', 'duplicate') OR transaction_id IS NOT NULL");
        });

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.SourceSha256).HasMaxLength(64);
        builder.Property(r => r.BankProfile).HasMaxLength(50);
        builder.Property(r => r.ParserVersion).HasMaxLength(50);
        builder.Property(r => r.TrustDecision).HasConversion<SnakeCaseEnumConverter<TrustDecision>>().HasMaxLength(20);
        builder.Property(r => r.Outcome).HasConversion<SnakeCaseEnumConverter<ParseOutcome>>().HasMaxLength(30);
        builder.Property(r => r.ReasonCode).HasMaxLength(50);
        builder.Property(r => r.ReasonMessage).HasMaxLength(500);
        builder.Property(r => r.TemplateId).HasMaxLength(100);
        builder.Property(r => r.SourcePart).HasConversion<LowercaseEnumConverter<SourcePart>>().HasMaxLength(10);
        builder.Property(r => r.RequestedBy).HasMaxLength(200);
        builder.Property(r => r.Worker).HasMaxLength(200);

        builder.HasOne<BankConnection>()
            .WithMany()
            .HasForeignKey(r => new { r.TenantId, r.BankConnectionId, r.BankAccountId })
            .HasPrincipalKey(b => new { b.TenantId, b.Id, b.BankAccountId })
            .OnDelete(DeleteBehavior.Restrict);

        // The transaction may have been established through another registration of the account.
        builder.HasOne<IncomingTransaction>()
            .WithMany()
            .HasForeignKey(r => new { r.TransactionId, r.TenantId, r.BankAccountId })
            .HasPrincipalKey(t => new { t.Id, t.TenantId, t.BankAccountId })
            .OnDelete(DeleteBehavior.Restrict);

        // Lets a source link reference its run together with the owner and receiving connection,
        // and a review member together with the owner and account.
        builder.HasAlternateKey(r => new { r.Id, r.TenantId, r.BankConnectionId });
        builder.HasAlternateKey(r => new { r.Id, r.TenantId, r.BankAccountId });

        builder.HasIndex(r => new { r.InboundMessageId, r.JobGeneration }).IsUnique().HasDatabaseName(GenerationUniqueIndex);
    }
}

internal sealed class IncomingTransactionConfiguration : IEntityTypeConfiguration<IncomingTransaction>
{
    public const string BankReferenceUniqueIndex = "incoming_transactions_account_bank_reference_unique";

    public void Configure(EntityTypeBuilder<IncomingTransaction> builder)
    {
        builder.ToTable("incoming_transactions", t =>
        {
            t.HasCheckConstraint("incoming_transactions_amount_positive", "amount > 0");
            t.HasCheckConstraint("incoming_transactions_currency_valid", "currency ~ '^[A-Z]{3}$'");
            t.HasCheckConstraint("incoming_transactions_symbol_valid", "variable_symbol IS NULL OR variable_symbol ~ '^[0-9]{1,10}$'");
            t.HasCheckConstraint(
                "incoming_transactions_symbol_key_consistent",
                "(variable_symbol IS NULL AND variable_symbol_key IS NULL) OR (variable_symbol IS NOT NULL AND variable_symbol_key IS NOT NULL)");
            t.HasCheckConstraint(
                "incoming_transactions_status_valid",
                $"ingestion_status IN ({SnakeCaseEnum<IngestionStatus>.SqlList})");
            t.HasCheckConstraint("incoming_transactions_source_kind_valid", "source_kind IN ('received', 'fixture')");
            t.HasCheckConstraint("incoming_transactions_environment_valid", "environment IN ('test', 'live')");
            t.HasCheckConstraint("incoming_transactions_origin_path_valid", $"origin_path IN ({SnakeCaseEnum<OriginPath>.SqlList})");
            t.HasCheckConstraint("incoming_transactions_fingerprint_format", "comparison_fingerprint ~ '^[0-9a-f]{64}$'");
            t.HasCheckConstraint("incoming_transactions_handling_valid", $"handling IN ({SnakeCaseEnum<TransactionHandling>.SqlList})");
            t.HasCheckConstraint(
                "incoming_transactions_canonical_consistent",
                "(handling = 'duplicate' AND canonical_transaction_id IS NOT NULL AND canonical_transaction_id <> id) "
                + "OR (handling <> 'duplicate' AND canonical_transaction_id IS NULL)");
            t.HasCheckConstraint("incoming_transactions_version_positive", "version >= 1");
        });

        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.Currency).HasMaxLength(3);
        builder.Property(t => t.VariableSymbol).HasMaxLength(10);
        builder.Property(t => t.VariableSymbolKey).HasMaxLength(10);
        builder.Property(t => t.BankTransactionId).HasMaxLength(BankMessageEvaluator.MaxReferenceLength);
        builder.Property(t => t.SenderAccount).HasMaxLength(30);
        builder.Property(t => t.SenderAccountRaw).HasMaxLength(100);
        builder.Property(t => t.SenderName).HasMaxLength(BankMessageEvaluator.MaxNameLength);
        builder.Property(t => t.Message).HasMaxLength(BankMessageEvaluator.MaxMessageLength);
        builder.Property(t => t.BankProfile).HasMaxLength(50);
        builder.Property(t => t.ParserVersion).HasMaxLength(50);
        builder.Property(t => t.IngestionStatus).HasConversion<SnakeCaseEnumConverter<IngestionStatus>>().HasMaxLength(30);
        builder.Property(t => t.ComparisonFingerprint).HasMaxLength(64);
        builder.Property(t => t.SourceKind).HasConversion<LowercaseEnumConverter<InboundSourceKind>>().HasMaxLength(20);
        builder.Property(t => t.Environment).HasConversion<LowercaseEnumConverter<InboundEnvironment>>().HasMaxLength(10);
        builder.Property(t => t.OriginPath).HasConversion<SnakeCaseEnumConverter<OriginPath>>().HasMaxLength(30);
        builder.Property(t => t.Handling).HasConversion<SnakeCaseEnumConverter<TransactionHandling>>().HasMaxLength(20);
        builder.Property(t => t.HandlingReason).HasMaxLength(500);
        builder.Property(t => t.HandledBy).HasMaxLength(200);
        builder.Property(t => t.AutoMatchBlockReason).HasMaxLength(50);
        builder.Property(t => t.MatchResult).HasMaxLength(50);

        // Status changes from concurrent runs never overwrite each other unseen.
        builder.Property<uint>("xmin").IsRowVersion();

        builder.HasOne<BankConnection>()
            .WithMany()
            .HasForeignKey(t => new { t.TenantId, t.BankConnectionId, t.BankAccountId })
            .HasPrincipalKey(b => new { b.TenantId, b.Id, b.BankAccountId })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasAlternateKey(t => new { t.Id, t.TenantId, t.BankConnectionId });
        builder.HasAlternateKey(t => new { t.Id, t.TenantId, t.BankAccountId });

        // A duplicate points to a record of the same owner and account, possibly of another of its connections.
        builder.HasOne<IncomingTransaction>()
            .WithMany()
            .HasForeignKey(t => new { t.CanonicalTransactionId, t.TenantId, t.BankAccountId })
            .HasPrincipalKey(t => new { t.Id, t.TenantId, t.BankAccountId })
            .OnDelete(DeleteBehavior.Restrict);

        // A proven bank reference identifies one movement per physical account and profile, across
        // all tenants and states (Feature 013). Retained transactions hold the claim.
        builder.HasIndex(t => new { t.BankAccountId, t.BankProfile, t.BankTransactionId })
            .IsUnique()
            .HasFilter("bank_transaction_id IS NOT NULL")
            .HasDatabaseName(BankReferenceUniqueIndex);

        builder.HasIndex(t => new { t.BankAccountId, t.ComparisonFingerprint }).HasDatabaseName("incoming_transactions_account_fingerprint_idx");
        builder.HasIndex(t => new { t.TenantId, t.Id }).HasDatabaseName("incoming_transactions_tenant_idx");
        builder.HasIndex(t => new { t.BankConnectionId, t.Id }).HasDatabaseName("incoming_transactions_connection_idx");
        builder.HasIndex(t => new { t.BankAccountId, t.VariableSymbolKey })
            .HasFilter("handling = 'unallocated'")
            .HasDatabaseName("incoming_transactions_unallocated_symbol_idx");
    }
}

internal sealed class TransactionSourceConfiguration : IEntityTypeConfiguration<TransactionSource>
{
    public const string MessageUniqueIndex = "transaction_sources_message_unique";

    public void Configure(EntityTypeBuilder<TransactionSource> builder)
    {
        builder.ToTable("transaction_sources");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();

        // The receiving connection may be another registration of the transaction's account.
        builder.HasOne<IncomingTransaction>()
            .WithMany()
            .HasForeignKey(s => new { s.TransactionId, s.TenantId, s.BankAccountId })
            .HasPrincipalKey(t => new { t.Id, t.TenantId, t.BankAccountId })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<BankConnection>()
            .WithMany()
            .HasForeignKey(s => new { s.TenantId, s.BankConnectionId, s.BankAccountId })
            .HasPrincipalKey(b => new { b.TenantId, b.Id, b.BankAccountId })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<MessageParseRun>()
            .WithMany()
            .HasForeignKey(s => new { s.ParseRunId, s.TenantId, s.BankConnectionId })
            .HasPrincipalKey(r => new { r.Id, r.TenantId, r.BankConnectionId })
            .OnDelete(DeleteBehavior.Restrict);

        // One movement per message in the initial scope.
        builder.HasIndex(s => s.InboundMessageId).IsUnique().HasDatabaseName(MessageUniqueIndex);
        builder.HasIndex(s => s.TransactionId).HasDatabaseName("transaction_sources_transaction_idx");
    }
}
