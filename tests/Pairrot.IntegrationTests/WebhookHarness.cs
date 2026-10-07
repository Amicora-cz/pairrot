using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pairrot.Infrastructure.Webhooks;

namespace Pairrot.IntegrationTests;

/// <summary>
/// The parsing host (Feature 007/008 synthetic profile, live SMTP-like instance, shiftable clock)
/// with a fake webhook receiver in place of the network. Deliveries run only when a test calls
/// <see cref="RunDeliveriesAsync"/>.
/// </summary>
public sealed class WebhookHarness
{
    private readonly PairrotFixture _fx;

    public WebhookHarness(PairrotFixture fx)
    {
        _fx = fx;
        Factory = fx.Parsing.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IWebhookTransport>();
            services.AddSingleton<IWebhookTransport>(Receiver);
        }));
    }

    public FakeWebhookReceiver Receiver { get; } = new();
    public WebApplicationFactory<Program> Factory { get; }
    public ShiftableTime Clock => _fx.Inbound.Clock;

    public HttpClient Client(string key) => _fx.Client(key, Factory);

    public async Task<int> RunDeliveriesAsync()
    {
        using var scope = Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WebhookDeliveryWorker>().RunDueAsync();
    }

    public async Task<T> WithServicesAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        using var scope = Factory.Services.CreateScope();
        return await action(scope.ServiceProvider);
    }
}

/// <summary>Records every request and answers from a per-URL script (default <c>200</c>).</summary>
public sealed class FakeWebhookReceiver : IWebhookTransport
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<Func<WebhookHttpRequest, WebhookHttpResponse>>> _scripts = new();

    public ConcurrentQueue<WebhookHttpRequest> Requests { get; } = new();

    public IReadOnlyList<WebhookHttpRequest> To(string url) => Requests.Where(r => r.Url.ToString() == url).ToList();

    /// <summary>The next answers for <paramref name="url"/>, in order.</summary>
    public void Script(string url, params Func<WebhookHttpRequest, WebhookHttpResponse>[] answers)
    {
        var queue = _scripts.GetOrAdd(url, _ => new());
        foreach (var answer in answers)
        {
            queue.Enqueue(answer);
        }
    }

    public static WebhookHttpResponse Status(int status, string? retryAfter = null) => new(status, null, null, "ok", retryAfter);

    public static WebhookHttpResponse Timeout() => new(null, "timeout", "The receiver did not answer within 10 seconds.", null, null);

    public Task<WebhookHttpResponse> SendAsync(WebhookHttpRequest request, CancellationToken ct)
    {
        Requests.Enqueue(request);
        var answer = _scripts.TryGetValue(request.Url.ToString(), out var queue) && queue.TryDequeue(out var next)
            ? next(request)
            : Status(200);
        return Task.FromResult(answer);
    }

    public static string Header(WebhookHttpRequest request, string name) => request.Headers.Single(h => h.Key == name).Value;
}

/// <summary>Thrown by a scripted answer to simulate a worker that dies after the receiver accepted the request.</summary>
public sealed class WorkerCrash() : Exception("Simulated worker crash.");
