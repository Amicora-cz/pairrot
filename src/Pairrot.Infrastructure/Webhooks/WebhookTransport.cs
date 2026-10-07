using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Pairrot.Core.Matching;
using Pairrot.Core.Webhooks;
using Pairrot.Infrastructure.Persistence;

namespace Pairrot.Infrastructure.Webhooks;

public sealed class WebhookOptions
{
    public const string Section = "Webhooks";
    public const string HttpClientName = "pairrot-webhooks";

    /// <summary>How often due deliveries are looked for.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How long a claimed delivery is reserved; longer than the 10-second request timeout.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Global concurrency; each endpoint has at most one request in flight.</summary>
    public int MaxParallel { get; set; } = 8;

    /// <summary>How often expired keys are retired, held deliveries rechecked, and old changes materialized.</summary>
    public TimeSpan MaintenanceInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Development/Test only: <c>host:port</c> destinations that may use HTTP and a private address,
    /// e.g. a local test receiver. Refused in other environments.
    /// </summary>
    public string[] AllowedLocalDestinations { get; set; } = [];

    public IReadOnlySet<string> AllowedLocal => AllowedLocalDestinations.Select(d => d.Trim().ToLowerInvariant()).ToHashSet();
}

public sealed class WebhookOptionsValidator(IHostEnvironment environment) : IValidateOptions<WebhookOptions>
{
    public ValidateOptionsResult Validate(string? name, WebhookOptions options) =>
        options.AllowedLocalDestinations.Length > 0 && !environment.IsDevelopment() && !environment.IsEnvironment("Testing")
            ? ValidateOptionsResult.Fail("Webhooks:AllowedLocalDestinations is only allowed in Development and Testing.")
            : ValidateOptionsResult.Success;
}

/// <summary>
/// Signing secrets must be recoverable to sign, so they are encrypted with the application's
/// Data Protection key ring (persisted in PostgreSQL), never stored in clear or only hashed.
/// </summary>
public class WebhookKeyRing(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("Pairrot.Webhooks.SigningKeys.v1");

    public string Protect(byte[] secret) => Convert.ToBase64String(_protector.Protect(secret));

    /// <summary><c>null</c> when the key ring cannot decrypt it (e.g. lost keys): the delivery is held, not sent unsigned.</summary>
    public byte[]? Unprotect(string protectedSecret)
    {
        try
        {
            return _protector.Unprotect(Convert.FromBase64String(protectedSecret));
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException)
        {
            return null;
        }
    }
}

/// <summary>
/// Creates the immutable event of a committed payment change and a delivery for each subscriber
/// at that moment, inside the change's database transaction. Shared advisory locks on the owner
/// and its parent serialize routing with endpoint edits, which take the same lock exclusively.
/// </summary>
public class WebhookRouter(PairrotDbContext db)
{
    public async Task RouteAsync(PaymentChange change, CancellationToken ct)
    {
        var environment = WebhookEvents.EnvironmentOf(change);
        var body = WebhookEvents.PaymentBody(change);
        db.WebhookEvents.Add(new WebhookEvent
        {
            Id = change.Id,
            Type = WebhookEvents.PaymentRequestUpdated,
            TenantId = change.TenantId,
            PaymentChangeId = change.Id,
            PaymentRequestId = change.PaymentRequestId,
            Sequence = change.Sequence,
            Environment = environment,
            Body = body,
            BodySha256 = WebhookEvents.Sha256(body),
            CreatedAt = change.CreatedAt,
        });

        var owner = await db.Tenants.AsNoTracking()
            .Where(t => t.Id == change.TenantId)
            .Select(t => new { t.ParentTenantId, ParentCanManage = t.ParentTenant != null && t.ParentTenant.CanCreateSubtenants })
            .SingleAsync(ct);
        foreach (var subscriber in new[] { change.TenantId, owner.ParentTenantId }.OfType<Guid>().Order())
        {
            await LockSharedAsync(db, subscriber, ct);
        }

        var parentId = owner.ParentCanManage ? owner.ParentTenantId : null;
        var endpoints = await db.WebhookEndpoints.AsNoTracking()
            .Where(e => e.Environment == environment
                && (e.TenantId == change.TenantId || (e.TenantId == parentId && e.Scope == WebhookScope.OwnAndDirectChildren)))
            .ToListAsync(ct);
        foreach (var endpoint in endpoints)
        {
            db.WebhookDeliveries.Add(NewDelivery(change.Id, endpoint, change.TenantId, change.CreatedAt));
        }
    }

    public static WebhookDelivery NewDelivery(Guid eventId, WebhookEndpoint endpoint, Guid ownerTenantId, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        EventId = eventId,
        EndpointId = endpoint.Id,
        SubscriberTenantId = endpoint.TenantId,
        OwnerTenantId = ownerTenantId,
        DestinationRevision = endpoint.DestinationRevision,
        DueAt = now,
        CreatedAt = now,
        UpdatedAt = now,
    };

    public static Task LockSharedAsync(PairrotDbContext db, Guid subscriberTenantId, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock_shared(hashtextextended({subscriberTenantId.ToString()}, 1010))", ct);

    public static Task LockExclusiveAsync(PairrotDbContext db, Guid subscriberTenantId, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({subscriberTenantId.ToString()}, 1010))", ct);
}

public sealed record WebhookHttpRequest(Uri Url, IReadOnlyList<KeyValuePair<string, string>> Headers, byte[] Body);

/// <param name="Status">The HTTP status, or <c>null</c> when no response arrived.</param>
/// <param name="ErrorCode"><c>timeout</c>, <c>network_error</c>, or <c>destination_not_allowed</c> without a status.</param>
public sealed record WebhookHttpResponse(int? Status, string? ErrorCode, string? ErrorMessage, string? Excerpt, string? RetryAfter);

/// <summary>Sends one signed request. The only implementation that touches the network is <see cref="HttpWebhookTransport"/>.</summary>
public interface IWebhookTransport
{
    Task<WebhookHttpResponse> SendAsync(WebhookHttpRequest request, CancellationToken ct);
}

public sealed class DestinationNotAllowedException(string message) : Exception(message);

/// <summary>
/// HTTPS POST with normal certificate validation, a 10-second timeout, no redirects, no cookies,
/// and a connection only to addresses validated as public at connect time, so DNS cannot change
/// between the check and the connection. TLS still verifies the original host name.
/// </summary>
public sealed class HttpWebhookTransport(IHttpClientFactory clients) : IWebhookTransport
{
    private const int MaxExcerptBytes = 4096;

    public async Task<WebhookHttpResponse> SendAsync(WebhookHttpRequest request, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, request.Url) { Content = new ByteArrayContent(request.Body) };
        message.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json; charset=utf-8");
        foreach (var (name, value) in request.Headers)
        {
            message.Headers.TryAddWithoutValidation(name, value);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(WebhookRetryPolicy.RequestTimeout);
        try
        {
            using var response = await clients.CreateClient(WebhookOptions.HttpClientName)
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var status = (int)response.StatusCode;
            string? excerpt = null;
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                var buffer = new byte[MaxExcerptBytes];
                var read = 0;
                int n;
                while (read < buffer.Length && (n = await stream.ReadAsync(buffer.AsMemory(read), timeout.Token)) > 0)
                {
                    read += n;
                }

                excerpt = SafeExcerpt(Encoding.UTF8.GetString(buffer, 0, read));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // A body that cannot be read does not turn an accepted delivery into a failure.
            }

            var retryAfter = response.Headers.RetryAfter is { } header
                ? header.Delta is { } delta ? ((long)delta.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture) : header.Date?.ToString("r")
                : null;
            return new WebhookHttpResponse(status, null, null, excerpt, retryAfter);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new WebhookHttpResponse(null, "timeout", "The receiver did not answer within 10 seconds.", null, null);
        }
        catch (HttpRequestException ex) when (Find<DestinationNotAllowedException>(ex) is { } blocked)
        {
            return new WebhookHttpResponse(null, "destination_not_allowed", blocked.Message, null, null);
        }
        catch (HttpRequestException ex)
        {
            return new WebhookHttpResponse(null, "network_error", Truncate($"The request failed: {ex.HttpRequestError}."), null, null);
        }
    }

    /// <summary>The handler for <see cref="WebhookOptions.HttpClientName"/>.</summary>
    public static SocketsHttpHandler CreateHandler(IReadOnlySet<string> allowedLocal) => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        ConnectTimeout = WebhookRetryPolicy.RequestTimeout,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectCallback = async (context, ct) =>
        {
            var host = context.DnsEndPoint.Host;
            var port = context.DnsEndPoint.Port;
            var local = allowedLocal.Contains($"{host.ToLowerInvariant()}:{port}");
            var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);
            if (addresses.Length == 0)
            {
                throw new DestinationNotAllowedException($"{host} has no address.");
            }

            if (!local && addresses.FirstOrDefault(a => !WebhookDestinations.IsPublic(a)) is { } blocked)
            {
                throw new DestinationNotAllowedException($"{host} resolves to a non-public address ({blocked}).");
            }

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                // Connect only to an address that was just validated.
                await socket.ConnectAsync(addresses, port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    /// <summary>Printable, bounded text; no headers are ever recorded.</summary>
    public static string SafeExcerpt(string text) =>
        Truncate(new string(text.Select(c => char.IsControl(c) && c is not ('\n' or '\t') ? '?' : c).ToArray()));

    private static string Truncate(string text) => text.Length <= 1000 ? text : text[..1000];

    private static T? Find<T>(Exception? ex) where T : Exception
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            if (ex is T match)
            {
                return match;
            }
        }

        return null;
    }
}
