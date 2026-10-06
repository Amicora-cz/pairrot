using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Pairrot.Core.Banking;
using Pairrot.Core.Tenants;
using Pairrot.Core.Users;
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

    /// <summary>ASP.NET Core Data Protection keys, so cookies survive application restarts.</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PairrotDbContext).Assembly);
    }
}
