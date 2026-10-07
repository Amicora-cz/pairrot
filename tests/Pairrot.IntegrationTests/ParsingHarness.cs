using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pairrot.Core.Banking;
using Pairrot.Core.Inbound;
using Pairrot.Core.Parsing;
using Pairrot.Infrastructure.Inbound;
using Pairrot.Infrastructure.Jobs;
using Pairrot.Infrastructure.Persistence;
using Pairrot.Tests.Shared;

namespace Pairrot.IntegrationTests;

/// <summary>
/// The Feature 006 host with the synthetic test profile installed for bank code 0800 in place of
/// the (still unverified) Česká spořitelna profile, plus an SMTP-style instance whose messages are
/// <c>received</c> and <c>live</c>. Nothing here represents a real bank format.
/// </summary>
public sealed class ParsingHarness
{
    /// <summary>An inline adapter standing in for a future SMTP receiving server.</summary>
    public const string SmtpInstanceId = "smtp-like";
    public const string SmtpDomain = "smtp.pairrot-fixture.net";

    /// <summary>A mail provider whose automatic forwards are authenticated by SPF in these tests.</summary>
    public const string VerifiedForwarderDomain = "mail.pairrot-fixture.net";

    public static readonly SenderAuthentication SmtpPassing =
        new("pass", "pass", "pass", "smtp_receiving_server", """{"spf":"pass","dkim":"pass","dmarc":"pass"}""");

    private readonly PairrotFixture _fx;

    public ParsingHarness(PairrotFixture fx)
    {
        _fx = fx;
        Factory = fx.Inbound.CreateFactory().WithWebHostBuilder(builder =>
        {
            var prefix = $"InboundEmail:Instances:{SmtpInstanceId}";
            builder.UseSetting($"{prefix}:Kind", FakeContentSource.FakeKind);
            builder.UseSetting($"{prefix}:Environment", "live");
            builder.UseSetting($"{prefix}:SourceKind", "received");
            builder.UseSetting($"{prefix}:Domain", SmtpDomain);
            builder.UseSetting("Forwarding:SpfVerifiedForwarderDomains:0", VerifiedForwarderDomain);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IBankProfile>();
                services.AddSingleton<IBankProfile>(Profile);
            });
        });
    }

    public SyntheticBankProfile Profile { get; } = new();
    public WebApplicationFactory<Program> Factory { get; }

    public HttpClient Client(string? key = null) => _fx.Client(key, Factory);

    public async Task<int> RunJobsAsync()
    {
        using var scope = Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<BackgroundJobRunner>().RunDueAsync();
    }

    public async Task<IntakeResult> AcceptAsync(InboundDelivery delivery)
    {
        using var scope = Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IInboundEmailIntake>().AcceptAsync(delivery, CancellationToken.None);
    }

    public async Task<T> WithServicesAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        using var scope = Factory.Services.CreateScope();
        return await action(scope.ServiceProvider);
    }

    public Task<T> WithDbAsync<T>(Func<PairrotDbContext, Task<T>> action) =>
        WithServicesAsync(services => action(services.GetRequiredService<PairrotDbContext>()));

    /// <summary>A new connection of the tenant with a ready address bound to <paramref name="instance"/>.</summary>
    public async Task<Account> CreateAccountAsync(Guid tenantId, string key, string instance = SmtpInstanceId)
    {
        var account = BankConnectionTests.NewAccount();
        var connection = await Client(key).PostAsJsonAsync("/v1/tenant/bank-connections", new { account_number = account.AccountNumber })
            .ExpectAsync(HttpStatusCode.Created);
        var connectionId = Guid.Parse(connection.Str("id"));
        var domain = instance == SmtpInstanceId ? SmtpDomain : "inline.pairrot.test";
        var address = $"p_{Guid.NewGuid():N}"[..26] + "@" + domain;
        await _fx.WithDbAsync(async db =>
        {
            db.InboundAddresses.Add(new InboundAddress
            {
                Id = Guid.CreateVersion7(), TenantId = tenantId, BankConnectionId = connectionId, Address = address,
                ProviderInstanceId = instance, ProviderKind = FakeContentSource.FakeKind,
                Environment = instance == SmtpInstanceId ? InboundEnvironment.Live : InboundEnvironment.Test,
                ProvisioningState = InboundProvisioningState.Ready, State = InboundAddressState.Active,
                CreatedAt = DateTimeOffset.UtcNow, ReadyAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        });
        return new Account(tenantId, key, connectionId, account, address, instance);
    }

    /// <summary>An inline delivery of <paramref name="text"/> to the account's address, like an SMTP handoff.</summary>
    public static InboundDelivery Delivery(
        Account account, string? text, string? html = null, string? from = SyntheticBankProfile.Sender,
        SenderAuthentication? authentication = null) => new()
    {
        ProviderInstanceId = account.Instance,
        SourceDeliveryId = $"d_{Guid.NewGuid():N}",
        SourceMessageId = $"m_{Guid.NewGuid():N}",
        ReceivedAt = DateTimeOffset.Parse("2026-10-07T07:16:00Z"),
        EnvelopeRecipients = [account.Address],
        RecipientEvidence = "smtp:rcpt_to",
        EnvelopeSender = "bounce@bank.pairrot-fixture.net",
        HeaderFrom = from,
        Subject = "Synthetic notice",
        TransportKind = "smtp",
        DeliveryEvidence = "test_harness",
        Content = new InboundContentSnapshot(text, html, """{"authentication-results":"dmarc=pass (forged)"}""", null, null,
            authentication ?? SmtpPassing),
    };

    /// <summary>Delivers a synthetic incoming notice and runs every due job; returns the message ID.</summary>
    public async Task<Guid> DeliverAsync(Account account, params (string, string?)[] fields) =>
        await DeliverAsync(Delivery(account, SyntheticBankProfile.Notice(fields)));

    public async Task<Guid> DeliverAsync(InboundDelivery delivery)
    {
        var accepted = await AcceptAsync(delivery);
        Assert.Equal((IntakeStatus.Accepted, InboundRoutingState.Routed), (accepted.Status, accepted.Routing));
        await RunJobsAsync();
        return accepted.MessageId!.Value;
    }

    public static (string, string?)[] Incoming(
        CzechBankAccount account, string amount = "450,00 CZK", string? vs = "2026000123", string? reference = null,
        string booked = "07.10.2026") =>
    [
        ("Type", "incoming"), ("Account", account.AccountNumber), ("Amount", amount), ("Booked", booked), ("Time", "07.10.2026 09:15"),
        ("VS", vs), ("Reference", reference), ("From account", "19-2000145399/0100"), ("From name", "Jana Nováková"),
    ];

    public Task<List<Core.Transactions.IncomingTransaction>> TransactionsAsync(Guid connectionId) =>
        WithDbAsync(db => db.IncomingTransactions.AsNoTracking().Where(t => t.BankConnectionId == connectionId).OrderBy(t => t.Id).ToListAsync());
}

public sealed record Account(Guid TenantId, string Key, Guid ConnectionId, CzechBankAccount Bank, string Address, string Instance);
