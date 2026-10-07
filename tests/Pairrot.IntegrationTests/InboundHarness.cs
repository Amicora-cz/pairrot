using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Pairrot.Core.Inbound;
using Pairrot.Infrastructure.Inbound;
using Pairrot.Infrastructure.Inbound.Resend;
using Pairrot.Infrastructure.Jobs;
using Svix;

namespace Pairrot.IntegrationTests;

/// <summary>
/// An API host whose default provider instance is a Resend instance with a fake Receiving API,
/// against the shared database, with an adjustable clock. It also configures a second Resend
/// instance and fake adapters (deferred, inline, remote setup) for the provider-independent
/// contract. Events are signed with the real Svix scheme and go through real verification. The
/// background worker is off: tests run due jobs explicitly, so retries are deterministic.
/// </summary>
public sealed class InboundHarness
{
    public const string InstanceId = "resend-test";
    public const string ReceivingDomain = "in.pairrot-fixture.net";
    public const string ApiKey = "re_test_receiving_key";
    public static readonly string WebhookSecret = "whsec_" + Convert.ToBase64String(Encoding.UTF8.GetBytes("pairrot-integration-test-secret!"));

    public const string SecondInstanceId = "resend-b";
    public const string SecondDomain = "in-b.pairrot-fixture.net";
    public static readonly string SecondSecret = "whsec_" + Convert.ToBase64String(Encoding.UTF8.GetBytes("pairrot-second-instance-secret!!"));

    public const string DeferredInstanceId = "fake-deferred";
    public const string InlineInstanceId = "fake-inline";
    public const string RemoteInstanceId = "remote-test";

    private readonly PairrotFixture _fx;

    public InboundHarness(PairrotFixture fx)
    {
        _fx = fx;
        Factory = CreateFactory();
    }

    public FakeResendApi Api { get; } = new();
    public ShiftableTime Clock { get; } = new();
    public FakeContentSource Sources { get; } = new();
    public FakeRemoteProvisioner Remote { get; } = new();
    public WebApplicationFactory<Program> Factory { get; }

    /// <summary>Another Resend-mode host on the same database and fakes (an application restart).</summary>
    public WebApplicationFactory<Program> CreateFactory(string? connectionString = null, bool worker = false, string defaultInstance = InstanceId) =>
        _fx.CreateFactory().WithWebHostBuilder(builder =>
        {
            if (connectionString is not null)
            {
                builder.UseSetting("ConnectionStrings:Pairrot", connectionString);
            }

            builder.UseSetting("InboundEmail:DefaultInstance", defaultInstance);
            Instance(builder, InstanceId, "resend", "live", ReceivingDomain,
                ("ApiKey", ApiKey), ("WebhookSecret", WebhookSecret), ("ApiBaseUrl", "https://resend.fake/"),
                ("DiscoveryPageSize", "2"), ("HistoryRetention", "30.00:00:00"));
            Instance(builder, SecondInstanceId, "resend", "live", SecondDomain,
                ("ApiKey", "re_second_key"), ("WebhookSecret", SecondSecret), ("ApiBaseUrl", "https://resend-b.fake/"));
            Instance(builder, DeferredInstanceId, FakeContentSource.FakeKind, "test", "deferred.pairrot.test");
            Instance(builder, InlineInstanceId, FakeContentSource.FakeKind, "test", "inline.pairrot.test");
            Instance(builder, RemoteInstanceId, FakeRemoteProvisioner.FakeKind, "live", "remote.pairrot-fixture.net");
            builder.UseSetting("BackgroundJobs:Enabled", worker ? "true" : "false");
            builder.UseSetting("BackgroundJobs:PollInterval", "00:00:00.100");
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<TimeProvider>(Clock);
                services.Configure<HttpClientFactoryOptions>(ResendContentSource.HttpClientName,
                    options => options.HttpMessageHandlerBuilderActions.Add(handler => handler.PrimaryHandler = Api));
                services.AddSingleton<IInboundContentSource>(Sources);
                services.AddSingleton<IInboundAddressProvisioner>(Sources);
                services.AddSingleton<IInboundAddressProvisioner>(Remote);
            });
        });

    /// <summary>Instances are fixtures: whatever delivers them, their messages are marked as such.</summary>
    private static void Instance(
        IWebHostBuilder builder, string id, string kind, string environment, string domain, params (string Key, string Value)[] settings)
    {
        var prefix = $"InboundEmail:Instances:{id}";
        builder.UseSetting($"{prefix}:Kind", kind);
        builder.UseSetting($"{prefix}:Environment", environment);
        builder.UseSetting($"{prefix}:SourceKind", "fixture");
        builder.UseSetting($"{prefix}:Domain", domain);
        foreach (var (key, value) in settings)
        {
            builder.UseSetting($"{prefix}:{key}", value);
        }
    }

    /// <summary>The common intake of this host, as any adapter would call it.</summary>
    public async Task<IntakeResult> AcceptAsync(InboundDelivery delivery)
    {
        using var scope = Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IInboundEmailIntake>().AcceptAsync(delivery, CancellationToken.None);
    }

    public HttpClient Client(string? key = null) => _fx.Client(key, Factory);

    /// <summary>Runs every due job in this host; returns how many ran.</summary>
    public async Task<int> RunJobsAsync(WebApplicationFactory<Program>? factory = null)
    {
        using var scope = (factory ?? Factory).Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<BackgroundJobRunner>().RunDueAsync();
    }

    /// <summary>A child of nalekci.cz with one bank connection and a live address.</summary>
    public async Task<Mailbox> CreateMailboxAsync()
    {
        var child = await _fx.CreateChildAsync();
        var client = Client(child.Key);
        var connection = await client.PostAsJsonAsync("/v1/tenant/bank-connections",
            new { account_number = BankConnectionTests.NewAccount().AccountNumber }).ExpectAsync(HttpStatusCode.Created);
        var connectionId = connection.Str("id");
        var assigned = await client.PostAsync($"/v1/tenant/bank-connections/{connectionId}/inbound-address", null)
            .ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("live_created", assigned.Str("address_status"));
        return new Mailbox(child.Id, child.Key, Guid.Parse(connectionId), assigned.Str("inbound_address"));
    }

    public static string NewEmailId() => Guid.NewGuid().ToString();

    /// <summary>A <c>GET /emails/receiving</c> list item, like Resend's documented example.</summary>
    public static string Listed(string emailId, DateTimeOffset createdAt, params string[] to) => JsonSerializer.Serialize(new
    {
        id = emailId,
        to,
        from = "info@csas.cz",
        created_at = createdAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
        subject = "Příchozí platba",
        bcc = Array.Empty<string>(),
        cc = Array.Empty<string>(),
        reply_to = Array.Empty<string>(),
        message_id = "<111-222-333@email.example.com>",
        attachments = Array.Empty<object>(),
    }, Raw);

    /// <summary>The sync state of the default Resend instance, with a chosen recovery range.</summary>
    public Task SetSyncAsync(DateTimeOffset syncFrom, DateTimeOffset? coveredThrough) => WithServiceAsync(async services =>
    {
        var state = await services.GetRequiredService<InboundDiscoveryService>().EnsureStateAsync(InstanceId);
        var db = services.GetRequiredService<Pairrot.Infrastructure.Persistence.PairrotDbContext>();
        var tracked = await db.InboundSyncStates.SingleAsync(x => x.Id == state.Id);
        tracked.SyncFrom = syncFrom;
        tracked.CoveredThrough = coveredThrough;
        tracked.CoverageGap = null;
        tracked.LastErrorCode = null;
        tracked.LastErrorMessage = null;
        await db.SaveChangesAsync();
        return 0;
    });

    public Task<InboundSyncState> SyncStateAsync() => WithServiceAsync(services =>
        services.GetRequiredService<Pairrot.Infrastructure.Persistence.PairrotDbContext>().InboundSyncStates.AsNoTracking()
            .SingleAsync(x => x.ProviderInstanceId == InstanceId));

    /// <summary>Queues a recovery check of the default Resend instance now and runs every due job.</summary>
    public async Task<int> DiscoverAsync(WebApplicationFactory<Program>? factory = null)
    {
        using (var scope = (factory ?? Factory).Services.CreateScope())
        {
            var queued = await scope.ServiceProvider.GetRequiredService<InboundDiscoveryService>().RunNowAsync(InstanceId, "test");
            Assert.Null(queued.Error);
        }

        return await RunJobsAsync(factory);
    }

    /// <summary>Runs every job that is due now or within <paramref name="horizon"/>, so a test starts with an empty queue.</summary>
    public async Task FlushJobsAsync(TimeSpan horizon)
    {
        Clock.Advance(horizon);
        await RunJobsAsync();
    }

    public async Task<T> WithServiceAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        using var scope = Factory.Services.CreateScope();
        return await action(scope.ServiceProvider);
    }

    /// <summary>Unescaped UTF-8 like provider payloads, so tests can alter text in the raw body.</summary>
    private static readonly JsonSerializerOptions Raw = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string ReceivedEvent(string emailId, params string[] to) => JsonSerializer.Serialize(new
    {
        type = "email.received",
        created_at = "2026-10-07T07:00:00.500Z",
        data = new
        {
            email_id = emailId,
            created_at = "2026-10-07T07:00:00.000Z",
            from = "Česká spořitelna <info@csas.cz>",
            to,
            bcc = Array.Empty<string>(),
            cc = Array.Empty<string>(),
            received_for = Array.Empty<string>(),
            message_id = "<111-222-333@email.example.com>",
            subject = "Příchozí platba",
            attachments = Array.Empty<object>(),
        },
    }, Raw);

    /// <summary>Posts <paramref name="body"/> with a valid signature, unless the caller overrides the headers.</summary>
    public Task<HttpResponseMessage> PostEventAsync(
        string body, string? eventId = null, DateTimeOffset? signedAt = null, string? signedBody = null, string? secret = null,
        WebApplicationFactory<Program>? factory = null, string instance = InstanceId)
    {
        eventId ??= $"msg_{Guid.NewGuid():N}";
        var timestamp = signedAt ?? DateTimeOffset.UtcNow;
        var signature = new Webhook(secret ?? WebhookSecret).Sign(eventId, timestamp, signedBody ?? body);
        var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/webhooks/resend/{instance}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("svix-id", eventId);
        request.Headers.Add("svix-timestamp", timestamp.ToUnixTimeSeconds().ToString());
        request.Headers.Add("svix-signature", signature);
        return (factory ?? Factory).CreateClient().SendAsync(request);
    }

    /// <summary>A Receiving API response for <paramref name="emailId"/>, like Resend's documented example.</summary>
    public static string Content(string emailId, string[] to, string text = "Na účet 19-2000145399/0800 přišla platba 450,00 CZK.",
        string? html = "<p>Na účet přišla <strong>platba</strong></p>") => JsonSerializer.Serialize(new
    {
        @object = "email",
        id = emailId,
        to,
        from = "info@csas.cz",
        created_at = "2026-10-07T07:00:00.000Z",
        subject = "Příchozí platba",
        html,
        text,
        headers = new Dictionary<string, string> { ["from"] = "Česká spořitelna <info@csas.cz>", ["authentication-results"] = "spf=pass (forged)" },
        bcc = Array.Empty<string>(),
        cc = Array.Empty<string>(),
        reply_to = Array.Empty<string>(),
        received_for = Array.Empty<string>(),
        authentication = new { spf = "pass", dkim = "pass", dmarc = "pass" },
        message_id = "<111-222-333@email.example.com>",
        raw = new { download_url = "https://example.resend.com/receiving/raw/x?Signature=secret-signature", expires_at = "2026-10-07T08:00:00Z" },
        attachments = new[] { new { id = "a1", filename = "vypis.pdf", content_type = "application/pdf", content_disposition = (string?)null, content_id = (string?)null, size = 1234 } },
    }, Raw);
}

public sealed record Mailbox(Guid TenantId, string Key, Guid ConnectionId, string Address);

/// <summary>Answers <c>GET /emails/receiving/{id}</c> from queued responses; the last response repeats.</summary>
public sealed class FakeResendApi : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<Func<HttpResponseMessage>>> _responses = new();
    private readonly ConcurrentDictionary<string, Func<HttpResponseMessage>> _last = new();

    public ConcurrentQueue<(string EmailId, string? Authorization, string Host)> Requests { get; } = new();

    private readonly ConcurrentDictionary<string, List<(string Id, DateTimeOffset CreatedAt, string Json)>> _listed = new();

    /// <summary>List requests as <c>(host, after)</c>.</summary>
    public ConcurrentQueue<(string Host, string? After)> ListRequests { get; } = new();

    /// <summary>When set, answers list requests before the listing; <c>null</c> falls through.</summary>
    public Func<string, string?, HttpResponseMessage?>? ListOverride { get; set; }

    /// <summary>Adds a received email to the listing of an API host; listings are newest first, like Resend's.</summary>
    public void List(string emailId, DateTimeOffset createdAt, string json, string host = "resend.fake")
    {
        var items = _listed.GetOrAdd(host, _ => []);
        lock (items)
        {
            items.Add((emailId, createdAt, json));
        }
    }

    private HttpResponseMessage ListPage(string host, int limit, string? after)
    {
        var items = _listed.GetOrAdd(host, _ => []);
        List<(string Id, DateTimeOffset CreatedAt, string Json)> ordered;
        lock (items)
        {
            ordered = items.OrderByDescending(i => i.CreatedAt).ThenBy(i => i.Id, StringComparer.Ordinal).ToList();
        }

        var start = after is null ? 0 : ordered.FindIndex(i => i.Id == after) + 1;
        var page = ordered.Skip(start).Take(limit).ToList();
        var body = $"{{\"object\":\"list\",\"has_more\":{(start + page.Count < ordered.Count ? "true" : "false")},\"data\":[{string.Join(",", page.Select(i => i.Json))}]}}";
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    public int CallsFor(string emailId) => Requests.Count(r => r.EmailId == emailId);

    public void Respond(string emailId, params Func<HttpResponseMessage>[] responses)
    {
        var queue = _responses.GetOrAdd(emailId, _ => new ConcurrentQueue<Func<HttpResponseMessage>>());
        foreach (var response in responses)
        {
            queue.Enqueue(response);
        }
    }

    public static Func<HttpResponseMessage> Ok(string body) => () => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    public static Func<HttpResponseMessage> Status(HttpStatusCode status, TimeSpan? retryAfter = null) => () =>
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent("{\"message\":\"secret detail\"}") };
        if (retryAfter is { } delay)
        {
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(delay);
        }

        return response;
    };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path == "/emails/receiving")
        {
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
            var host = request.RequestUri.Host;
            ListRequests.Enqueue((host, query["after"]));
            return Task.FromResult(ListOverride?.Invoke(host, query["after"]) ?? ListPage(host, int.Parse(query["limit"] ?? "100"), query["after"]));
        }

        const string prefix = "/emails/receiving/";
        Assert.StartsWith(prefix, path);
        var emailId = Uri.UnescapeDataString(path[prefix.Length..]);
        Requests.Enqueue((emailId, request.Headers.Authorization?.ToString(), request.RequestUri.Host));

        if (_responses.TryGetValue(emailId, out var queue) && queue.TryDequeue(out var next))
        {
            _last[emailId] = next;
            return Task.FromResult(next());
        }

        return Task.FromResult(_last.TryGetValue(emailId, out var repeat) ? repeat() : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

/// <summary>
/// A provider kind for contract tests: instantly ready addresses and deferred content from a
/// dictionary. Its instances may also deliver inline through common intake.
/// </summary>
public sealed class FakeContentSource : IInboundContentSource, IInboundAddressProvisioner
{
    public const string FakeKind = "fake";

    private readonly ConcurrentDictionary<string, ContentRetrieval> _content = new();

    public string Kind => FakeKind;
    public bool RequiresRemoteSetup => false;
    public ConcurrentQueue<string> Retrieved { get; } = new();

    public void Set(string reference, ContentRetrieval result) => _content[reference] = result;

    public Task<ContentRetrieval> RetrieveAsync(InboundProviderInstance instance, string reference, CancellationToken ct)
    {
        Retrieved.Enqueue($"{instance.Id}/{reference}");
        return Task.FromResult(_content.GetValueOrDefault(reference) ?? new ContentRetrieval(ContentRetrievalOutcome.NotYetAvailable, Error: "Not yet."));
    }

    public IEnumerable<string> Validate(InboundProviderInstance instance) => [];

    public Task<ProvisioningResult> EnsureAddressAsync(InboundProviderInstance instance, string address, CancellationToken ct) =>
        Task.FromResult(new ProvisioningResult(true));
}

/// <summary>A provider kind whose addresses need remote setup; tests queue its results.</summary>
public sealed class FakeRemoteProvisioner : IInboundAddressProvisioner
{
    public const string FakeKind = "fake-remote";

    public string Kind => FakeKind;
    public bool RequiresRemoteSetup => true;
    public ConcurrentQueue<ProvisioningResult> Results { get; } = new();
    public ConcurrentQueue<string> Calls { get; } = new();

    public IEnumerable<string> Validate(InboundProviderInstance instance) => [];

    public Task<ProvisioningResult> EnsureAddressAsync(InboundProviderInstance instance, string address, CancellationToken ct)
    {
        Calls.Enqueue(address);
        return Task.FromResult(Results.TryDequeue(out var result) ? result : new ProvisioningResult(true));
    }
}

/// <summary>The system clock plus an offset that tests move forward.</summary>
public sealed class ShiftableTime : TimeProvider
{
    private long _offsetTicks;

    public void Advance(TimeSpan by) => Interlocked.Add(ref _offsetTicks, by.Ticks);

    public override DateTimeOffset GetUtcNow() => base.GetUtcNow().AddTicks(Interlocked.Read(ref _offsetTicks));
}

internal static class InboundJson
{
    public static JsonArray Items(this JsonNode list) => list["items"]!.AsArray();
}
