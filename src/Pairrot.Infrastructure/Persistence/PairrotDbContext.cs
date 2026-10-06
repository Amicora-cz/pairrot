using Microsoft.EntityFrameworkCore;
using Pairrot.Core.Tenants;

namespace Pairrot.Infrastructure.Persistence;

public class PairrotDbContext(DbContextOptions<PairrotDbContext> options) : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<TenantApiKey> ApiKeys => Set<TenantApiKey>();
    public DbSet<TenantAuditEvent> AuditEvents => Set<TenantAuditEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PairrotDbContext).Assembly);
}
