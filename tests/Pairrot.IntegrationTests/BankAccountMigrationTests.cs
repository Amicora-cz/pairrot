using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Pairrot.Core.Banking;
using Pairrot.Core.Parsing;
using Pairrot.Infrastructure.Persistence;

namespace Pairrot.IntegrationTests;

/// <summary>
/// Feature 013's migration on a database seeded in the previous schema: archived, deleted-tenant,
/// supplied-symbol, and automatic-symbol data are converted without changing IDs, financial
/// facts, symbols, or allocations; comparison fingerprints become v2; a failed self-check rolls
/// the whole migration back.
/// </summary>
[Collection(PairrotCollection.Name)]
public class BankAccountMigrationTests(PairrotFixture fx)
{
    private const string PreviousMigration = "20261007205833_AutomaticVariableSymbols";

    private static readonly Guid Root = Guid.CreateVersion7(), Active = Guid.CreateVersion7(), Deleted = Guid.CreateVersion7();
    private static readonly Guid Current = Guid.CreateVersion7(), Archived = Guid.CreateVersion7(), Released = Guid.CreateVersion7();
    private static readonly Guid SuppliedRequest = Guid.CreateVersion7(), AutomaticRequest = Guid.CreateVersion7(), DeletedRequest = Guid.CreateVersion7();
    private static readonly Guid Referenced = Guid.CreateVersion7(), Lookalike = Guid.CreateVersion7(), Allocation = Guid.CreateVersion7();

    [Fact] // AC 8
    public async Task The_migration_converts_existing_data_and_recomputes_fingerprints()
    {
        await using var db = await CreateDatabaseAsync();
        var (current, archived, released) = (BankConnectionTests.NewAccount(), BankConnectionTests.NewAccount(), BankConnectionTests.NewAccount());
        await db.Database.ExecuteSqlRawAsync(Seed(current, archived, released, consistent: true));

        await db.GetService<IMigrator>().MigrateAsync();

        var connections = InOrder(await db.BankConnections.AsNoTracking().ToListAsync(), c => c.Id, Current, Archived, Released);
        var accounts = await db.BankAccounts.AsNoTracking().ToDictionaryAsync(a => a.Id);
        Assert.Equal(3, accounts.Count);
        Assert.All(connections, c => Assert.Equal(c.Iban, accounts[c.BankAccountId].Iban));
        Assert.Equal(current.AccountNumber, accounts[connections[0].BankAccountId].AccountNumber);
        Assert.Equal(current.BankCode, accounts[connections[0].BankAccountId].BankCode);
        Assert.Equal(
            [(BankConnectionStatus.Active, (BankConnectionArchivedReason?)null), (BankConnectionStatus.Archived, BankConnectionArchivedReason.User),
                (BankConnectionStatus.Archived, BankConnectionArchivedReason.TenantDeleted)],
            connections.Select(c => (c.Status, c.ArchivedReason)));
        Assert.All(connections.Skip(1), c => Assert.NotNull(c.ArchivedAt));
        var accountOf = connections.ToDictionary(c => c.Id, c => c.BankAccountId);

        var requests = InOrder(await db.PaymentRequests.AsNoTracking().ToListAsync(), p => p.Id, SuppliedRequest, AutomaticRequest, DeletedRequest);
        Assert.Equal(
            [(SuppliedRequest, "0042", "42", 45000L, accountOf[Current]), (AutomaticRequest, "1", "1", 30000L, accountOf[Current]),
                (DeletedRequest, "42", "42", 45000L, accountOf[Released])],
            requests.Select(p => (p.Id, p.VariableSymbol, p.VariableSymbolKey, p.Amount, p.BankAccountId)));
        var allocator = Assert.Single(await db.VariableSymbolAllocators.AsNoTracking().ToListAsync());
        Assert.Equal((accountOf[Current], 2L), (allocator.BankAccountId, allocator.NextCandidate));

        var transactions = InOrder(await db.IncomingTransactions.AsNoTracking().ToListAsync(), t => t.Id, Referenced, Lookalike);
        Assert.Equal([(Referenced, 45000L, "R-1", accountOf[Current]), (Lookalike, 45000L, (string?)null, accountOf[Released])],
            transactions.Select(t => (t.Id, t.Amount, t.BankTransactionId, t.BankAccountId)));
        // The v2 fingerprint the parser computes for a new notification of the same movement through any
        // registration of the account, so a re-delivered pre-migration movement finds its lookalike.
        Assert.All(transactions, t => Assert.Equal(
            BankMessageEvaluator.ComparisonFingerprint(t.BankProfile, t.BankAccountId, t.Amount, t.Currency, t.BookedOn, t.VariableSymbolKey),
            t.ComparisonFingerprint));

        var allocation = Assert.Single(await db.PaymentAllocations.AsNoTracking().ToListAsync());
        Assert.Equal((Allocation, Referenced, SuppliedRequest, Current, Current, accountOf[Current], 45000L),
            (allocation.Id, allocation.TransactionId, allocation.PaymentRequestId, allocation.BankConnectionId,
                allocation.PaymentRequestBankConnectionId, allocation.BankAccountId, allocation.Amount));
        Assert.Equal(45000L, requests[0].PaidAmount);
    }

    [Fact] // AC 8
    public async Task A_failed_self_check_rolls_the_whole_migration_back()
    {
        await using var db = await CreateDatabaseAsync();
        await db.Database.ExecuteSqlRawAsync(Seed(BankConnectionTests.NewAccount(), BankConnectionTests.NewAccount(),
            BankConnectionTests.NewAccount(), consistent: false));

        var error = await Assert.ThrowsAnyAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync());
        Assert.Contains("self-check failed", error.MessageText);
        Assert.Contains("paid amount differs", error.MessageText);

        var applied = await db.Database.GetAppliedMigrationsAsync();
        Assert.Equal(PreviousMigration, applied.Last());
        var tables = await db.Database.SqlQueryRaw<string>(
            "SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_name = 'bank_accounts'").ToListAsync();
        Assert.Empty(tables);
    }

    /// <summary>Exactly the given records, in the given order (UUIDv7 made in one millisecond do not sort by creation).</summary>
    private static List<T> InOrder<T>(List<T> items, Func<T, Guid> id, params Guid[] ids)
    {
        Assert.Equal(ids.Order(), items.Select(id).Order());
        return ids.Select(i => items.Single(x => id(x) == i)).ToList();
    }

    /// <summary>A fresh database in the fixture's container, migrated to the schema before Feature 013.</summary>
    private async Task<PairrotDbContext> CreateDatabaseAsync()
    {
        var name = $"migration_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(fx.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {name}", admin);
            await create.ExecuteNonQueryAsync();
        }

        var builder = new NpgsqlConnectionStringBuilder(fx.ConnectionString) { Database = name };
        var options = new DbContextOptionsBuilder<PairrotDbContext>().UseNpgsql(builder.ConnectionString).UseSnakeCaseNamingConvention().Options;
        var db = new PairrotDbContext(options);
        await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        return db;
    }

    /// <summary>
    /// Old-schema rows: an active and an archived connection of an active tenant, an active
    /// connection of a deleted tenant, supplied and automatic symbols (the same symbol on another
    /// account), a referenced allocated movement, and a reference-free v1-fingerprinted movement.
    /// <paramref name="consistent"/> <c>false</c> leaves a paid amount without its allocation.
    /// </summary>
    private static string Seed(CzechBankAccount current, CzechBankAccount archived, CzechBankAccount released, bool consistent)
    {
        const string now = "'2026-10-01T08:00:00Z'";
        const string fingerprint = "'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'";
        const string qr = "'SPD*1.0', 'SPAYD', 'spayd-1.0'";
        var allocated = consistent ? "allocated" : "unallocated";
        var allocation = consistent
            ? $"""
               INSERT INTO payment_allocations (id, tenant_id, bank_connection_id, transaction_id, payment_request_id, amount, currency,
                   method, reason, evidence, state, created_by, created_at, version)
               VALUES ('{Allocation}', '{Active}', '{Current}', '{Referenced}', '{SuppliedRequest}', 45000, 'CZK', 'automatic', NULL, 'live',
                   'active', 'system', {now}, 1);
               """
            : "";
        return $"""
            INSERT INTO tenants (id, parent_tenant_id, external_id, name, status, can_create_subtenants, can_create_campaigns, created_at, updated_at, deleted_at)
            VALUES ('{Root}', NULL, 'root-{Root:N}', 'Root', 'active', true, false, {now}, {now}, NULL),
                   ('{Active}', '{Root}', 'active', 'Active studio', 'active', false, false, {now}, {now}, NULL),
                   ('{Deleted}', '{Root}', 'deleted', 'Deleted studio', 'deleted', false, false, {now}, {now}, '2026-10-05T10:00:00Z');

            INSERT INTO bank_connections (id, tenant_id, label, account_number, iban, currency, status, created_at, updated_at, archived_at)
            VALUES ('{Current}', '{Active}', NULL, '{current.AccountNumber}', '{current.Iban}', 'CZK', 'active', {now}, {now}, NULL),
                   ('{Archived}', '{Active}', 'Old', '{archived.AccountNumber}', '{archived.Iban}', 'CZK', 'archived', {now}, {now}, {now}),
                   ('{Released}', '{Deleted}', NULL, '{released.AccountNumber}', '{released.Iban}', 'CZK', 'active', {now}, {now}, NULL);

            INSERT INTO payment_requests (id, tenant_id, bank_connection_id, external_id, account_number, iban, amount, currency,
                variable_symbol, variable_symbol_key, variable_symbol_source, status, paid_amount, change_sequence, creation_fingerprint,
                qr_payload, qr_format, qr_generator_version, created_at, updated_at, version)
            VALUES ('{SuppliedRequest}', '{Active}', '{Current}', 'supplied', '{current.AccountNumber}', '{current.Iban}', 45000, 'CZK',
                    '0042', '42', 'supplied', 'paid', 45000, 1, {fingerprint}, {qr}, {now}, {now}, 2),
                   ('{AutomaticRequest}', '{Active}', '{Current}', 'automatic', '{current.AccountNumber}', '{current.Iban}', 30000, 'CZK',
                    '1', '1', 'automatic', 'open', 0, 0, {fingerprint}, {qr}, {now}, {now}, 1),
                   ('{DeletedRequest}', '{Deleted}', '{Released}', 'deleted', '{released.AccountNumber}', '{released.Iban}', 45000, 'CZK',
                    '42', '42', 'supplied', 'open', 0, 0, {fingerprint}, {qr}, {now}, {now}, 1);

            INSERT INTO variable_symbol_allocators (bank_connection_id, next_candidate) VALUES ('{Current}', 2);

            INSERT INTO incoming_transactions (id, tenant_id, bank_connection_id, amount, currency, variable_symbol, variable_symbol_key,
                bank_transaction_id, booked_on, received_at, bank_profile, parser_version, ingestion_status, ingestion_reasons,
                comparison_fingerprint, source_kind, environment, handling, created_at, updated_at)
            VALUES ('{Referenced}', '{Active}', '{Current}', 45000, 'CZK', '0042', '42', 'R-1', '2026-10-01', {now}, 'synthetic',
                    'synthetic-1', 'accepted', '[]', {fingerprint}, 'received', 'live', '{allocated}', {now}, {now}),
                   ('{Lookalike}', '{Deleted}', '{Released}', 45000, 'CZK', '42', '42', NULL, '2026-10-01', {now}, 'synthetic',
                    'synthetic-1', 'accepted', '[]', {fingerprint}, 'received', 'live', 'unallocated', {now}, {now});
            {allocation}
            """;
    }
}
