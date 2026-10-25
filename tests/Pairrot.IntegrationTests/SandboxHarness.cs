using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pairrot.Core.Banking;
using Pairrot.Core.Transactions;
using Pairrot.Infrastructure.Jobs;
using Pairrot.Infrastructure.Persistence;
using Pairrot.Infrastructure.Webhooks;

namespace Pairrot.IntegrationTests;

/// <summary>
/// The API with <c>Sandbox:Enabled</c> (Feature 021) and the built-in <c>stub</c> test instance,
/// the real bank profiles, and a fake webhook receiver in place of the network. The worker is off;
/// tests run jobs and deliveries explicitly.
/// </summary>
public sealed class SandboxHarness
{
    private readonly PairrotFixture _fx;

    public SandboxHarness(PairrotFixture fx)
    {
        _fx = fx;
        Factory = fx.CreateFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Sandbox:Enabled", "true");
            builder.UseSetting("BackgroundJobs:Enabled", "false");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IWebhookTransport>();
                services.AddSingleton<IWebhookTransport>(Receiver);
            });
        });
    }

    public FakeWebhookReceiver Receiver { get; } = new();
    public WebApplicationFactory<Program> Factory { get; }

    public HttpClient Client(string key) => _fx.Client(key, Factory);

    public async Task RunJobsAsync()
    {
        using var scope = Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<BackgroundJobRunner>().RunDueAsync();
    }

    public async Task RunDeliveriesAsync()
    {
        using var scope = Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<WebhookDeliveryWorker>().RunDueAsync();
    }

    public async Task<T> WithDbAsync<T>(Func<PairrotDbContext, Task<T>> action)
    {
        using var scope = Factory.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<PairrotDbContext>());
    }

    /// <summary>A new connection of the tenant for a fresh account of <paramref name="bankCode"/>; it has no address yet.</summary>
    public async Task<SandboxAccount> CreateAccountAsync(string key, string bankCode = "0800")
    {
        var bank = BankConnectionTests.NewAccount() with { BankCode = bankCode };
        var connection = await Client(key).PostAsJsonAsync("/v1/tenant/bank-connections", new { account_number = bank.AccountNumber })
            .ExpectAsync(HttpStatusCode.Created);
        return new SandboxAccount(key, Guid.Parse(connection.Str("id")), bank);
    }

    public Task<List<IncomingTransaction>> TransactionsAsync(Guid connectionId) =>
        WithDbAsync(db => db.IncomingTransactions.AsNoTracking().Where(t => t.BankConnectionId == connectionId).OrderBy(t => t.Id).ToListAsync());
}

public sealed record SandboxAccount(string Key, Guid ConnectionId, CzechBankAccount Bank);
