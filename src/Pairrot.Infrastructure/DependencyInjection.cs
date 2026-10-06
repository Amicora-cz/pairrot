using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pairrot.Infrastructure.Persistence;
using Pairrot.Infrastructure.Tenants;

namespace Pairrot.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "Pairrot";

    public static IServiceCollection AddPairrotInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException(
                $"Connection string '{ConnectionStringName}' is not configured (ConnectionStrings__{ConnectionStringName}).");

        services.AddDbContext<PairrotDbContext>(options => ConfigureDbContext(options, connectionString));
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<TenantService>();
        services.AddScoped<RootTenantAdministration>();
        return services;
    }

    internal static void ConfigureDbContext(DbContextOptionsBuilder options, string connectionString) =>
        options.UseNpgsql(connectionString).UseSnakeCaseNamingConvention();
}
