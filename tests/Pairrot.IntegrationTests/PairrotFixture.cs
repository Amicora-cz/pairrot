using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pairrot.Core.Tenants;
using Pairrot.Infrastructure.Persistence;
using Pairrot.Infrastructure.Tenants;
using Pairrot.Infrastructure.Users;
using Testcontainers.PostgreSql;

namespace Pairrot.IntegrationTests;

/// <summary>
/// One PostgreSQL container and one API host for all integration tests. The nalekci.cz root
/// is created through the same administration code as the CLI. Tests create their own children
/// with unique external IDs, so they do not depend on each other.
/// </summary>
public sealed class PairrotFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;
    public string RootTenantKey { get; private set; } = null!;
    public string RootManagementKey { get; private set; } = null!;
    public Guid RootId { get; private set; }

    public string ConnectionString => _postgres.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        Factory = CreateFactory();

        using var scope = Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<PairrotDbContext>().Database.MigrateAsync();

        var setup = await SetupRootAsync(RootTenantAdministration.NaLekciExternalId);
        RootId = setup.Tenant.Id;
        RootTenantKey = setup.TenantKey!.Secret;
        RootManagementKey = setup.ManagementKey!.Secret;
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    /// <summary>A fresh API host against the same database (simulates an application restart).</summary>
    public WebApplicationFactory<Program> CreateFactory(int loginRateLimitPermits = 100_000) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Pairrot", ConnectionString);
            // All test logins share one client address; throttling has its own test.
            builder.UseSetting("Auth:LoginRateLimitPermits", loginRateLimitPermits.ToString(CultureInfo.InvariantCulture));
        });

    public const string Password = "correct horse battery staple";

    /// <summary>Creates a user with <see cref="Password"/> and admin memberships in the given tenants.</summary>
    public async Task<string> CreateUserAsync(params Guid[] tenantIds)
    {
        var email = $"{Guid.NewGuid():N}@users.example.com";
        using var scope = Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserAdministration>();
        var created = await users.CreateUserAsync(email, Password, "integration-tests");
        Assert.Null(created.Error);
        foreach (var tenantId in tenantIds)
        {
            var granted = await users.GrantAdminAsync(email, tenantId.ToString(), "integration-tests");
            Assert.Null(granted.Error);
        }

        return email;
    }

    public async Task<T> WithUsersAsync<T>(Func<UserAdministration, Task<T>> action)
    {
        using var scope = Factory.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<UserAdministration>());
    }

    /// <summary>A browser signed in as a new user who administers the given tenants.</summary>
    public async Task<BrowserClient> SignInAsync(params Guid[] tenantIds)
    {
        var browser = new BrowserClient(Factory);
        await browser.LoginAsync(await CreateUserAsync(tenantIds));
        return browser;
    }

    public async Task<RootSetupResult> SetupRootAsync(string externalId)
    {
        using var scope = Factory.Services.CreateScope();
        var admin = scope.ServiceProvider.GetRequiredService<RootTenantAdministration>();
        var result = await admin.SetupAsync(externalId, externalId, null, "integration-tests");
        return result.Value ?? throw new InvalidOperationException(result.Error!.Message);
    }

    public async Task WithDbAsync(Func<PairrotDbContext, Task> action)
    {
        using var scope = Factory.Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<PairrotDbContext>());
    }

    public Task SetStatusAsync(Guid tenantId, TenantStatus status) => WithDbAsync(db =>
        db.Tenants.Where(t => t.Id == tenantId).ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, status)));

    public HttpClient Client(string? key = null, WebApplicationFactory<Program>? factory = null)
    {
        var client = (factory ?? Factory).CreateClient();
        if (key is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        return client;
    }

    /// <summary>Creates a child of nalekci.cz and returns its ID and first tenant key.</summary>
    public async Task<(Guid Id, string Key, Guid KeyId)> CreateChildAsync(
        string? externalId = null, string? email = null, string? managementKey = null)
    {
        var response = await Client(managementKey ?? RootManagementKey).PostAsJsonAsync("/v1/subtenants", new
        {
            external_id = externalId ?? Unique("child"),
            name = "Test child",
            contact_email = email,
        });
        response.EnsureSuccessStatusCode();
        var body = (await response.Content.ReadFromJsonAsync<JsonNode>())!;
        return (
            Guid.Parse(body["tenant"]!["id"]!.GetValue<string>()),
            body["api_key"]!["secret"]!.GetValue<string>(),
            Guid.Parse(body["api_key"]!["id"]!.GetValue<string>()));
    }

    public static string Unique(string prefix) => $"{prefix}_{Guid.NewGuid():N}";
}

[CollectionDefinition(Name)]
public sealed class PairrotCollection : ICollectionFixture<PairrotFixture>
{
    public const string Name = "Pairrot";
}
