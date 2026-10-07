using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Pairrot.Core.Banking;
using Pairrot.Core.Inbound;
using Pairrot.Core.Jobs;
using Pairrot.Core.Matching;
using Pairrot.Core.Payments;
using Pairrot.Core.Tenants;
using Pairrot.Core.Transactions;
using Pairrot.Core.Users;
using Pairrot.Core.Webhooks;
using Pairrot.Infrastructure.Users;

namespace Pairrot.Infrastructure.Persistence;

public class PairrotDbContext(DbContextOptions<PairrotDbContext> options)
    : IdentityUserContext<PairrotUser, Guid>(options), IDataProtectionKeyContext
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<TenantApiKey> ApiKeys => Set<TenantApiKey>();
    public DbSet<TenantAuditEvent> AuditEvents => Set<TenantAuditEvent>();
    public DbSet<TenantMembership> Memberships => Set<TenantMembership>();
    public DbSet<UserSession> Sessions => Set<UserSession>();
    public DbSet<UserAuditEvent> UserAuditEvents => Set<UserAuditEvent>();
    public DbSet<BankConnection> BankConnections => Set<BankConnection>();
    public DbSet<PaymentRequest> PaymentRequests => Set<PaymentRequest>();
    public DbSet<InboundAddress> InboundAddresses => Set<InboundAddress>();
    public DbSet<InboundDeliveryReceipt> InboundDeliveryReceipts => Set<InboundDeliveryReceipt>();
    public DbSet<InboundMessage> InboundMessages => Set<InboundMessage>();
    public DbSet<BackgroundJob> BackgroundJobs => Set<BackgroundJob>();
    public DbSet<InboundSyncState> InboundSyncStates => Set<InboundSyncState>();
    public DbSet<MessageParseRun> MessageParseRuns => Set<MessageParseRun>();
    public DbSet<IncomingTransaction> IncomingTransactions => Set<IncomingTransaction>();
    public DbSet<TransactionSource> TransactionSources => Set<TransactionSource>();
    public DbSet<ForwardingSource> ForwardingSources => Set<ForwardingSource>();
    public DbSet<PaymentAllocation> PaymentAllocations => Set<PaymentAllocation>();
    public DbSet<PaymentChange> PaymentChanges => Set<PaymentChange>();
    public DbSet<PaymentReviewCase> ReviewCases => Set<PaymentReviewCase>();
    public DbSet<PaymentReviewCaseMember> ReviewCaseMembers => Set<PaymentReviewCaseMember>();
    public DbSet<PaymentReviewDecision> ReviewDecisions => Set<PaymentReviewDecision>();
    public DbSet<ManualCommandReceipt> CommandReceipts => Set<ManualCommandReceipt>();
    public DbSet<WebhookEndpoint> WebhookEndpoints => Set<WebhookEndpoint>();
    public DbSet<WebhookDestinationRevision> WebhookDestinationRevisions => Set<WebhookDestinationRevision>();
    public DbSet<WebhookSigningKey> WebhookSigningKeys => Set<WebhookSigningKey>();
    public DbSet<WebhookEvent> WebhookEvents => Set<WebhookEvent>();
    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();
    public DbSet<WebhookDeliveryAttempt> WebhookDeliveryAttempts => Set<WebhookDeliveryAttempt>();

    /// <summary>ASP.NET Core Data Protection keys, so cookies survive application restarts.</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PairrotDbContext).Assembly);
    }
}
