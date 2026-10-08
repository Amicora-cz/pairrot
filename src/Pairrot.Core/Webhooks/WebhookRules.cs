using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pairrot.Core.Inbound;
using Pairrot.Core.Matching;
using Pairrot.Core.Payments;

namespace Pairrot.Core.Webhooks;

/// <summary>
/// The signature scheme. Signing input: the ASCII delivery ID, <c>.</c>, the decimal Unix
/// timestamp, <c>.</c>, and the raw UTF-8 body bytes, with nothing added. HMAC-SHA256 with the
/// decoded secret bytes, standard Base64. Header: <c>v1,&lt;key-id&gt;,&lt;base64&gt;</c>, entries
/// separated by one space during key rotation.
/// </summary>
public static class WebhookSignature
{
    public const string SecretPrefix = "pwhsec_";
    public const string KeyIdPrefix = "pwhk_";
    public const int SecretBytes = 32;

    /// <summary>Receivers reject timestamps further than this from their clock.</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    public static (string KeyId, byte[] Secret) NewKey() =>
        (KeyIdPrefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)), RandomNumberGenerator.GetBytes(SecretBytes));

    /// <summary>The one-time display form <c>pwhsec_&lt;base64url&gt;</c>.</summary>
    public static string FormatSecret(byte[] secret) =>
        SecretPrefix + Convert.ToBase64String(secret).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[]? ParseSecret(string formatted)
    {
        if (!formatted.StartsWith(SecretPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var text = formatted[SecretPrefix.Length..].Replace('-', '+').Replace('_', '/');
        text = text.PadRight(text.Length + (4 - text.Length % 4) % 4, '=');
        try
        {
            var bytes = Convert.FromBase64String(text);
            return bytes.Length == SecretBytes ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static string Sign(byte[] secret, string deliveryId, long timestamp, byte[] body)
    {
        var prefix = Encoding.ASCII.GetBytes($"{deliveryId}.{timestamp.ToString(CultureInfo.InvariantCulture)}.");
        var input = new byte[prefix.Length + body.Length];
        prefix.CopyTo(input, 0);
        body.CopyTo(input, prefix.Length);
        return Convert.ToBase64String(HMACSHA256.HashData(secret, input));
    }

    public static string Header(IEnumerable<(string KeyId, byte[] Secret)> keys, string deliveryId, long timestamp, byte[] body) =>
        string.Join(' ', keys.Select(k => $"v1,{k.KeyId},{Sign(k.Secret, deliveryId, timestamp, body)}"));

    /// <summary>
    /// The receiver's check, before parsing JSON: a fresh timestamp and a matching signature of an
    /// accepted key, compared in constant time.
    /// </summary>
    public static bool Verify(
        IReadOnlyDictionary<string, byte[]> acceptedKeys, string? deliveryId, string? timestampText, string? header, byte[] body,
        DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(deliveryId) || header is null
            || !long.TryParse(timestampText, NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp)
            || Math.Abs(now.ToUnixTimeSeconds() - timestamp) > Tolerance.TotalSeconds)
        {
            return false;
        }

        foreach (var entry in header.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = entry.Split(',');
            if (parts is ["v1", var keyId, var signature] && acceptedKeys.TryGetValue(keyId, out var secret))
            {
                var expected = Encoding.ASCII.GetBytes(Sign(secret, deliveryId, timestamp, body));
                if (CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(signature)))
                {
                    return true;
                }
            }
        }

        return false;
    }
}

/// <summary>Feature 011: four attempts per generation, the retries 5 s, 60 s, and 1 h after the preceding failure.</summary>
public static class WebhookRetryPolicy
{
    public const int MaxAttempts = 4;

    public static readonly TimeSpan[] Delays = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(60), TimeSpan.FromHours(1)];

    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How a response is classified (Feature 010 section 6).</summary>
    public static WebhookAttemptOutcome Classify(int status) => status switch
    {
        >= 200 and < 300 => WebhookAttemptOutcome.Delivered,
        408 or 425 or 429 or >= 500 => WebhookAttemptOutcome.RetryableFailure,
        _ => WebhookAttemptOutcome.PermanentFailure,
    };

    /// <summary>
    /// When the next attempt may start after a retryable failure of attempt <paramref name="attempt"/>
    /// (1-based), or <c>null</c> when the budget is spent. <c>Retry-After</c> may only delay it.
    /// </summary>
    public static DateTimeOffset? NextAttempt(int attempt, DateTimeOffset failedAt, DateTimeOffset? retryAfter = null)
    {
        if (attempt < 1 || attempt >= MaxAttempts)
        {
            return null;
        }

        var due = failedAt + Delays[attempt - 1];
        return retryAfter is { } requested && requested > due ? requested : due;
    }

    /// <summary>Parses <c>Retry-After</c> (seconds or an HTTP date); malformed or overflowing values are ignored.</summary>
    public static DateTimeOffset? RetryAfter(string? value, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim();
        if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return seconds <= 7 * 24 * 3600 ? now.AddSeconds(seconds) : null;
        }

        return DateTimeOffset.TryParseExact(value, "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            && date <= now.AddDays(7)
                ? date
                : null;
    }
}

/// <summary>Destination rules that need no DNS: absolute HTTPS, no credentials or fragment, no private literal address.</summary>
public static class WebhookDestinations
{
    public const int MaxUrlLength = 2000;

    /// <summary>A validation message, or <c>null</c> when the URL may be saved.</summary>
    /// <param name="allowedLocal">Development/Test-only <c>host:port</c> entries that may use HTTP and a private address.</param>
    public static string? Validate(string? url, IReadOnlySet<string> allowedLocal)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > MaxUrlLength || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return "url must be an absolute HTTPS URL.";
        }

        if (allowedLocal.Contains($"{uri.Host}:{uri.Port}") && uri.Scheme is "http" or "https")
        {
            return uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 ? "url must not contain credentials or a fragment." : null;
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            return "url must use HTTPS.";
        }

        if (uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
        {
            return "url must not contain credentials or a fragment.";
        }

        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6 && IPAddress.TryParse(uri.IdnHost.Trim('[', ']'), out var literal)
            && !IsPublic(literal))
        {
            return "url must point to a public address.";
        }

        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (host is "localhost" || host.EndsWith(".localhost", StringComparison.Ordinal) || host.EndsWith(".local", StringComparison.Ordinal)
            || host.EndsWith(".internal", StringComparison.Ordinal) || !host.Contains('.'))
        {
            return "url must point to a public host name.";
        }

        return null;
    }

    /// <summary>Whether an address is publicly routable: no loopback, private, link-local, metadata, CGNAT, multicast, or reserved ranges.</summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 0 || b[0] == 10 || b[0] == 127 || b[0] >= 224
                || (b[0] == 100 && b[1] >= 64 && b[1] < 128)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 172 && b[1] >= 16 && b[1] < 32)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 192 && b[1] == 0 && b[2] is 0 or 2)
                || (b[0] == 198 && b[1] is 18 or 19)
                || (b[0] == 198 && b[1] == 51 && b[2] == 100)
                || (b[0] == 203 && b[1] == 0 && b[2] == 113));
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            return !(IPAddress.IsLoopback(address) || address.Equals(IPAddress.IPv6None) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal
                || address.IsIPv6Multicast || address.IsIPv6Teredo
                || (b[0] & 0xfe) == 0xfc // unique local fc00::/7
                || (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8) // documentation
                || (b[0] == 0x00 && b.Take(10).All(x => x == 0)) // ::/80 incl. IPv4-compatible
                || (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b)); // NAT64 to an unchecked IPv4
        }

        return false;
    }
}

/// <summary>The <c>payment_request.updated</c> body, built once from a committed payment change.</summary>
public static class WebhookEvents
{
    public const string PaymentRequestUpdated = "payment_request.updated";
    public const string Test = "webhook.test";
    public const int SchemaVersion = 1;
    public const int MaxBodyBytes = 64 * 1024;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>The stable cause vocabulary of the event contract.</summary>
    public static string Cause(PaymentChangeCause cause) => cause switch
    {
        PaymentChangeCause.AutomaticAllocation or PaymentChangeCause.ManualAllocation => "allocation_created",
        PaymentChangeCause.AllocationRevoked => "allocation_revoked",
        PaymentChangeCause.TransactionReviewRequired => "transaction_review_required",
        PaymentChangeCause.Cancelled => "request_cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(cause)),
    };

    /// <summary>Test and fixture receipts are test changes; a cancellation, which has no receipt, is live.</summary>
    public static InboundEnvironment EnvironmentOf(PaymentChange change) =>
        change.Evidence is null or "live" ? InboundEnvironment.Live : InboundEnvironment.Test;

    public static string PaymentBody(PaymentChange change) => JsonSerializer.Serialize(new
    {
        id = change.Id,
        type = PaymentRequestUpdated,
        schema_version = SchemaVersion,
        created_at = change.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFF'Z'", CultureInfo.InvariantCulture),
        environment = EnvironmentOf(change) == InboundEnvironment.Live ? "live" : "test",
        tenant_id = change.TenantId,
        data = new
        {
            payment_request_id = change.PaymentRequestId,
            external_id = change.ExternalId,
            bank_connection_id = change.BankConnectionId,
            bank_account_id = change.BankAccountId,
            sequence = change.Sequence,
            cause = Cause(change.Cause),
            previous = Summary(change.StatusBefore, change.PaidBefore, change.RemainingBefore, change.OverpaidBefore),
            current = Summary(change.StatusAfter, change.PaidAfter, change.RemainingAfter, change.OverpaidAfter),
            expected_amount = change.ExpectedAmount,
            currency = change.Currency,
        },
    }, Json);

    public static string TestBody(Guid eventId, Guid tenantId, DateTimeOffset createdAt) => JsonSerializer.Serialize(new
    {
        id = eventId,
        type = Test,
        schema_version = SchemaVersion,
        created_at = createdAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFF'Z'", CultureInfo.InvariantCulture),
        environment = "test",
        tenant_id = tenantId,
        data = new { synthetic = true, message = "A test delivery from Pairrot. Do not update any reservation." },
    }, Json);

    public static string Sha256(string body) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)));

    private static object Summary(PaymentRequestStatus status, long paid, long? remaining, long overpaid) => new
    {
        status = JsonNamingPolicy.SnakeCaseLower.ConvertName(status.ToString()),
        paid_amount = paid,
        remaining_amount = remaining,
        overpaid_amount = overpaid,
    };
}
