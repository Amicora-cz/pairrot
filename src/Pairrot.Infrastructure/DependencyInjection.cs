using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Pairrot.Core.Parsing;
using Pairrot.Infrastructure.Banking;
using Pairrot.Infrastructure.Erasure;
using Pairrot.Infrastructure.Inbound;
using Pairrot.Infrastructure.Inbound.Resend;
using Pairrot.Infrastructure.Jobs;
using Pairrot.Infrastructure.Matching;
using Pairrot.Infrastructure.Payments;
using Pairrot.Infrastructure.Persistence;
using Pairrot.Infrastructure.Tenants;
using Pairrot.Infrastructure.Transactions;
using Pairrot.Infrastructure.Users;
using Pairrot.Infrastructure.Webhooks;

namespace Pairrot.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "Pairrot";

    public static IServiceCollection AddPairrotInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException(
                $"Connection string '{ConnectionStringName}' is not configured (ConnectionStrings__{ConnectionStringName}).");

        // Feature 016: every save records the events of the transactions, review cases, and addresses it changes.
        services.AddSingleton<SubjectEventInterceptor>();
        // Saved jobs and webhook deliveries wake their workers, which otherwise sleep until the next due time.
        services.AddSingleton<WorkSignals>();
        services.AddSingleton<WorkSignalInterceptor>();
        services.AddDbContext<PairrotDbContext>((sp, options) =>
        {
            ConfigureDbContext(options, connectionString);
            options.AddInterceptors(sp.GetRequiredService<SubjectEventInterceptor>(), sp.GetRequiredService<WorkSignalInterceptor>());
        });
        services.AddSingleton(TimeProvider.System);

        // Cookie-protection keys and webhook signing secrets use one key ring in PostgreSQL, so both survive restarts.
        services.AddDataProtection()
            .SetApplicationName("Pairrot")
            .PersistKeysToDbContext<PairrotDbContext>();
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

        // Feature 008: forwarded notifications and foreign-mail screening.
        services.Configure<ForwardingOptions>(configuration.GetSection(ForwardingOptions.Section));
        services.AddScoped<SenderScreening>();
        services.AddScoped<InboundRetention>();
        services.AddScoped<ForwardingService>();

        services.Configure<BackgroundJobOptions>(configuration.GetSection(BackgroundJobOptions.Section));
        services.AddScoped<BackgroundJobQueue>();
        services.AddScoped<BackgroundJobRunner>();

        services.AddScoped<BankConnectionService>();
        services.AddScoped<PaymentQrService>();
        services.AddScoped<PaymentRequestService>();

        // Feature 009: matching, manual allocation, review, and the payment-change journal.
        services.AddOptions<MatchingOptions>()
            .Bind(configuration.GetSection(MatchingOptions.Section))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<MatchingOptions>, MatchingOptionsValidator>();
        services.AddScoped<PaymentLedger>();
        services.AddScoped<ReviewCaseRecorder>();
        services.AddScoped<PaymentMatcher>();
        services.AddScoped<IBackgroundJobHandler, MatchTransactionJob>();
        services.AddScoped<IBackgroundJobHandler, MatchPaymentRequestJob>();
        services.AddScoped<MatchingScheduling>();
        services.AddScoped<ManualCommands>();
        services.AddScoped<PaymentMatchingService>();
        services.AddScoped<ReviewCaseService>();

        // Features 010 and 011: outgoing payment-status webhooks with a fixed retry schedule.
        services.AddOptions<WebhookOptions>()
            .Bind(configuration.GetSection(WebhookOptions.Section))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<WebhookOptions>, WebhookOptionsValidator>();
        services.AddHttpClient(WebhookOptions.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(sp => HttpWebhookTransport.CreateHandler(sp.GetRequiredService<IOptions<WebhookOptions>>().Value.AllowedLocal))
            .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddSingleton<IWebhookTransport, HttpWebhookTransport>();
        services.AddSingleton<WebhookKeyRing>();
        services.AddScoped<WebhookRouter>();
        services.AddScoped<WebhookDeliveryWorker>();
        services.AddScoped<WebhookMaintenance>();
        services.AddScoped<WebhookService>();
        services.AddScoped<AttentionService>();

        // Feature 014: tenant erasure and payer-data retention.
        services.Configure<ErasureOptions>(configuration.GetSection(ErasureOptions.Section));
        services.Configure<RetentionOptions>(configuration.GetSection(RetentionOptions.Section));
        services.AddScoped<TenantErasureService>();
        services.AddScoped<IBackgroundJobHandler, TenantErasureJob>();
        services.AddScoped<PayerDataRetention>();
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
            services.AddHostedService<InboundRetentionScheduler>();
            services.AddHostedService<MatchingScheduler>();
            if (configuration.GetSection(WebhookOptions.Section).Get<WebhookOptions>()?.DispatcherEnabled ?? true)
            {
                services.AddHostedService<WebhookDispatcher>();
            }

            services.AddHostedService<PayerDataRetentionScheduler>();
        }

        return services;
    }

    internal static void ConfigureDbContext(DbContextOptionsBuilder options, string connectionString) =>
        options.UseNpgsql(connectionString).UseSnakeCaseNamingConvention();
}
