using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Pairrot.Infrastructure.Persistence;

/// <summary>Used by <c>dotnet ef</c>; adding migrations does not need a running app.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<PairrotDbContext>
{
    public PairrotDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Pairrot")
            ?? "Host=localhost;Port=5433;Database=pairrot;Username=pairrot;Password=pairrot";

        var options = new DbContextOptionsBuilder<PairrotDbContext>();
        DependencyInjection.ConfigureDbContext(options, connectionString);
        return new PairrotDbContext(options.Options);
    }
}
