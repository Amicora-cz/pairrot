using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Pairrot.Core.Parsing;
using Pairrot.Infrastructure.Banking;
using Pairrot.Infrastructure.Inbound;
using Pairrot.Infrastructure.Inbound.Resend;
using Pairrot.Infrastructure.Jobs;
using Pairrot.Infrastructure.Payments;
using Pairrot.Infrastructure.Persistence;
using Pairrot.Infrastructure.Tenants;
using Pairrot.Infrastructure.Transactions;
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

        // Provider instances come from configuration; adapters register by kind. Stub addresses by
        // default; real reception only with an explicitly configured live instance.
        services.AddOptions<InboundEmailOptions>()
            .Bind(configuration.GetSection(InboundEmailOptions.Section))
            .PostConfigure(InboundEmailOptions.AddBuiltInStub)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<InboundEmailOptions>, InboundEmailOptionsValidator>();
        services.AddSingleton<InboundProviderRegistry>();
        services.AddSingleton<IInboundAddressProvisioner, StubAddressProvisioner>();
        services.AddSingleton<IInboundAddressProvisioner, ResendAddressProvisioner>();
        services.AddSingleton<ResendApi>();
        services.AddSingleton<IInboundContentSource, ResendContentSource>();
        services.AddSingleton<IInboundMessageDiscovery, ResendDiscovery>();
        services.AddHttpClient(ResendApi.HttpClientName);
        services.AddScoped<ResendWebhookAdapter>();

        services.AddScoped<IInboundEmailIntake, InboundIntake>();
        services.AddScoped<InboundRouter>();
        services.AddScoped<InboundFetchQueue>();
        services.AddScoped<InboundMessageService>();
        services.AddScoped<InboundAdministration>();
        services.AddScoped<IBackgroundJobHandler, InboundContentFetcher>();
        services.Configure<InboundDiscoveryOptions>(configuration.GetSection(InboundDiscoveryOptions.Section));
        services.AddScoped<InboundDiscoveryService>();
        services.AddScoped<IBackgroundJobHandler, InboundDiscoveryJob>();

        // Feature 007: bank profiles are code, selected by bank code; tenants cannot add or choose them.
        services.AddSingleton<IBankProfile, CeskaSporitelnaProfile>();
        services.AddSingleton<BankProfileRegistry>();
        services.Configure<MessageParsingOptions>(configuration.GetSection(MessageParsingOptions.Section));
        services.AddScoped<IBackgroundJobHandler, MessageParseJob>();
        services.AddScoped<ParseScheduling>();
        services.AddScoped<IncomingTransactionService>();

        services.Configure<BackgroundJobOptions>(configuration.GetSection(BackgroundJobOptions.Section));
        services.AddScoped<BackgroundJobQueue>();
        services.AddScoped<BackgroundJobRunner>();

        services.AddScoped<BankConnectionService>();
        services.AddScoped<PaymentQrService>();
        services.AddScoped<PaymentRequestService>();
        return services;
    }

    /// <summary>Runs due background jobs in this process unless <c>BackgroundJobs:Enabled</c> is false.</summary>
    public static IServiceCollection AddPairrotBackgroundWorker(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(BackgroundJobOptions.Section).Get<BackgroundJobOptions>() ?? new BackgroundJobOptions();
        if (options.Enabled)
        {
            services.AddHostedService<BackgroundJobWorker>();
            services.AddHostedService<InboundDiscoveryScheduler>();
            services.AddHostedService<MessageParseScheduler>();
        }

        return services;
    }

    internal static void ConfigureDbContext(DbContextOptionsBuilder options, string connectionString) =>
        options.UseNpgsql(connectionString).UseSnakeCaseNamingConvention();
}
