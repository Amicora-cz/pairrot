using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pairrot.Infrastructure.Persistence;
using Pairrot.Infrastructure.Tenants;
using Pairrot.Infrastructure.Users;

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

        var authSection = configuration.GetSection(PairrotAuthOptions.Section);
        services.Configure<PairrotAuthOptions>(authSection);
        var auth = authSection.Get<PairrotAuthOptions>() ?? new PairrotAuthOptions();

        // Identity stores users and verifies passwords; tenant access stays in Pairrot's own model.
        services
            .AddIdentityCore<PairrotUser>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.User.AllowedUserNameCharacters = string.Empty; // The user name is the validated email.
                options.Password.RequiredLength = auth.PasswordMinLength;
                options.Password.RequireDigit = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequiredUniqueChars = 1;
                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = auth.LockoutMaxFailedAttempts;
                options.Lockout.DefaultLockoutTimeSpan = auth.LockoutDuration;
            })
            .AddEntityFrameworkStores<PairrotDbContext>();
        services.AddScoped<AuthService>();
        services.AddScoped<UserAdministration>();
        return services;
    }

    internal static void ConfigureDbContext(DbContextOptionsBuilder options, string connectionString) =>
        options.UseNpgsql(connectionString).UseSnakeCaseNamingConvention();
}
