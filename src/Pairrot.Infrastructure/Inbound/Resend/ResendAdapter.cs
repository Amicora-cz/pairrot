using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Configuration;
using Pairrot.Core.Inbound;
using Svix;
using Svix.Exceptions;

namespace Pairrot.Infrastructure.Inbound.Resend;

/// <summary>Settings of a <c>resend</c> instance, under <c>InboundEmail:Instances:{id}</c>. Server-side only.</summary>
public sealed class ResendSettings
{
    public const string Kind = "resend";

    /// <summary>API key used only by the background worker to retrieve received content.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>The webhook endpoint's signing secret (<c>whsec_...</c>).</summary>
    public string WebhookSecret { get; set; } = "";

    public string ApiBaseUrl { get; set; } = "https://api.resend.com/";

    /// <summary>Limit for a webhook request body (default 1 MiB). Webhooks carry metadata only.</summary>
    public int MaxWebhookBytes { get; set; } = 1024 * 1024;

    /// <summary>Limit for one Receiving API response on the wire (default 5 MiB).</summary>
    public int MaxContentBytes { get; set; } = 5 * 1024 * 1024;

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Page size of the recovery check's message listing (1-100, default 100).</summary>
    public int DiscoveryPageSize { get; set; } = 100;

    /// <summary>How long the account keeps received emails, if known; older history is reported as a gap.</summary>
    public TimeSpan? HistoryRetention { get; set; }

    public static ResendSettings Of(InboundProviderInstance instance) => instance.Settings.Get<ResendSettings>() ?? new ResendSettings();
}

/// <summary>
/// Resend receives for a whole domain, so a new address needs no provider call: Pairrot's own
/// reservation is all the setup there is.
/// </summary>
public sealed class ResendAddressProvisioner : IInboundAddressProvisioner
{
    public string Kind => ResendSettings.Kind;
    public bool RequiresRemoteSetup => false;

    public IEnumerable<string> Validate(InboundProviderInstance instance)
    {
        var settings = ResendSettings.Of(instance);
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            yield return "ApiKey is required.";
        }

        if (!settings.WebhookSecret.StartsWith("whsec_", StringComparison.Ordinal))
        {
            yield return "WebhookSecret must be the endpoint's signing secret (whsec_...).";
        }

        if (!Uri.TryCreate(settings.ApiBaseUrl, UriKind.Absolute, out var baseUrl) || baseUrl.Scheme is not ("https" or "http"))
        {
            yield return "ApiBaseUrl must be an absolute URL.";
        }

        if (settings.MaxWebhookBytes < 1024 || settings.MaxContentBytes < 1024 || settings.RequestTimeout <= TimeSpan.Zero)
        {
            yield return "size limits must be at least 1024 bytes and RequestTimeout positive.";
        }

        if (settings.DiscoveryPageSize is < 1 or > 100 || settings.HistoryRetention <= TimeSpan.Zero)
        {
            yield return "DiscoveryPageSize must be 1-100 and HistoryRetention positive.";
        }
    }

    public Task<ProvisioningResult> EnsureAddressAsync(InboundProviderInstance instance, string address, CancellationToken ct) =>
        Task.FromResult(new ProvisioningResult(true));
}

public enum ResendApiStatus
{
    Ok,
    NotFound,
    Unauthorized,
    Temporary,
    TooLarge,
    Invalid,
}

/// <param name="Body">The UTF-8 body, only for <see cref="ResendApiStatus.Ok"/>.</param>
/// <param name="Error">A safe description: never the body, a header value, or the API key.</param>
public sealed record ResendApiResponse(ResendApiStatus Status, string? Body = null, string? Error = null, TimeSpan? RetryAfter = null);

/// <summary>
/// Authenticated <c>GET</c> requests to an instance's Resend API. Responses are streamed with the
/// instance's size limit and classified; nothing above this class sees HTTP.
/// </summary>
public sealed class ResendApi(IHttpClientFactory httpClients)
{
    public const string HttpClientName = "resend";
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public async Task<ResendApiResponse> GetAsync(InboundProviderInstance instance, string relativeUrl, CancellationToken ct)
    {
        var settings = ResendSettings.Of(instance);
        var baseUrl = settings.ApiBaseUrl.EndsWith('/') ? settings.ApiBaseUrl : settings.ApiBaseUrl + "/";
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(baseUrl), relativeUrl));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(settings.RequestTimeout);

        try
        {
            using var response = await httpClients.CreateClient(HttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var status = (int)response.StatusCode;
            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                    break;
                case HttpStatusCode.NotFound:
                    return new ResendApiResponse(ResendApiStatus.NotFound, Error: "The provider has no such resource (404).");
                case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                    return new ResendApiResponse(ResendApiStatus.Unauthorized, Error: $"The provider rejected Pairrot's credentials ({status}).");
                case HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout:
                case >= HttpStatusCode.InternalServerError:
                    return new ResendApiResponse(ResendApiStatus.Temporary,
                        Error: $"The provider is temporarily unavailable ({status}).", RetryAfter: RetryAfter(response));
                default:
                    return new ResendApiResponse(ResendApiStatus.Invalid, Error: $"The provider answered with an unexpected status ({status}).");
            }

            var limit = settings.MaxContentBytes;
            if (response.Content.Headers.ContentLength > limit)
            {
                return TooLarge(limit);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, timeout.Token)) > 0)
            {
                if (buffer.Length + read > limit)
                {
                    return TooLarge(limit);
                }

                buffer.Write(chunk, 0, read);
            }

            try
            {
                return new ResendApiResponse(ResendApiStatus.Ok, StrictUtf8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
            }
            catch (DecoderFallbackException)
            {
                return new ResendApiResponse(ResendApiStatus.Invalid, Error: "The provider response is not valid UTF-8.");
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ResendApiResponse(ResendApiStatus.Temporary, Error: "The provider did not answer in time.");
        }
        catch (HttpRequestException ex)
        {
            return new ResendApiResponse(ResendApiStatus.Temporary, Error: $"The provider could not be reached ({ex.HttpRequestError}).");
        }
    }

    private static ResendApiResponse TooLarge(int limit) =>
        new(ResendApiStatus.TooLarge, Error: $"The response exceeds the limit of {limit} bytes and was not stored.");

    private static TimeSpan? RetryAfter(HttpResponseMessage response) => response.Headers.RetryAfter switch
    {
        { Delta: { } delta } => delta,
        { Date: { } date } when date > DateTimeOffset.UtcNow => date - DateTimeOffset.UtcNow,
        _ => null,
    };
}

/// <summary>Retrieves content with <c>GET /emails/receiving/{id}</c> on the message's own instance and maps it into the common snapshot.</summary>
public sealed class ResendContentSource(ResendApi api) : IInboundContentSource
{
    public const string HttpClientName = ResendApi.HttpClientName;

    public string Kind => ResendSettings.Kind;

    public async Task<ContentRetrieval> RetrieveAsync(InboundProviderInstance instance, string reference, CancellationToken ct)
    {
        var response = await api.GetAsync(instance, $"emails/receiving/{Uri.EscapeDataString(reference)}", ct);
        return response.Status switch
        {
            ResendApiStatus.Ok => ResendPayloads.ParseContent(response.Body!, reference) is { } snapshot
                ? new ContentRetrieval(ContentRetrievalOutcome.Retrieved, snapshot)
                : new ContentRetrieval(ContentRetrievalOutcome.Invalid, Error: "The provider response is not the requested email."),
            ResendApiStatus.NotFound => new ContentRetrieval(ContentRetrievalOutcome.NotYetAvailable, Error: response.Error),
            ResendApiStatus.Unauthorized => new ContentRetrieval(ContentRetrievalOutcome.ConfigurationError, Error: response.Error),
            ResendApiStatus.Temporary => new ContentRetrieval(ContentRetrievalOutcome.TemporaryFailure, Error: response.Error, RetryAfter: response.RetryAfter),
            ResendApiStatus.TooLarge => new ContentRetrieval(ContentRetrievalOutcome.TooLarge, Error: response.Error),
            _ => new ContentRetrieval(ContentRetrievalOutcome.Invalid, Error: response.Error),
        };
    }
}

/// <summary>
/// Lists received emails with the paginated <c>GET /emails/receiving</c> (cursor <c>after</c>,
/// <c>has_more</c>) and submits each one through common intake with a deferred content reference.
/// The API documents no date filter, so scanning stops early only when every page so far was in
/// descending reception order and a whole page lies before the requested start; otherwise it reads
/// to the last page.
/// </summary>
public sealed class ResendDiscovery(ResendApi api, TimeProvider time) : IInboundMessageDiscovery
{
    public const string TransportKind = "resend_list_api";
    public const string DeliveryEvidence = "provider_api_access";

    public string Kind => ResendSettings.Kind;

    public async Task<DiscoveryResult> DiscoverAsync(InboundProviderInstance instance, DateTimeOffset from, IDiscoverySink sink, CancellationToken ct)
    {
        var settings = ResendSettings.Of(instance);
        string? cursor = null;
        DateTimeOffset? previous = null;
        var descending = true;
        while (true)
        {
            var url = $"emails/receiving?limit={settings.DiscoveryPageSize}" + (cursor is null ? "" : $"&after={Uri.EscapeDataString(cursor)}");
            var response = await api.GetAsync(instance, url, ct);
            switch (response.Status)
            {
                case ResendApiStatus.Ok:
                    break;
                case ResendApiStatus.Temporary:
                    return new DiscoveryResult(DiscoveryOutcome.TemporaryFailure, response.Error, response.RetryAfter);
                case ResendApiStatus.Unauthorized:
                    return new DiscoveryResult(DiscoveryOutcome.ConfigurationError, response.Error);
                default:
                    return new DiscoveryResult(DiscoveryOutcome.Invalid, response.Error);
            }

            if (ResendPayloads.ParseList(response.Body!) is not { } page)
            {
                return new DiscoveryResult(DiscoveryOutcome.Invalid, "The provider's message list is malformed.");
            }

            var allBefore = page.Items.Count > 0;
            foreach (var item in page.Items)
            {
                if (previous is { } last && item.CreatedAt > last)
                {
                    descending = false;
                }

                previous = item.CreatedAt ?? previous;
                if (item.CreatedAt is { } created && created < from)
                {
                    continue;
                }

                allBefore = false;
                if (!await sink.AcceptAsync(ToDelivery(instance, item), ct))
                {
                    return new DiscoveryResult(DiscoveryOutcome.Aborted, "A discovered message could not be recorded.");
                }
            }

            if (!await sink.PageCompletedAsync(ct))
            {
                return new DiscoveryResult(DiscoveryOutcome.Aborted, "The scan lost its lease.");
            }

            if (!page.HasMore || page.Items.Count == 0 || (descending && allBefore))
            {
                break;
            }

            cursor = page.Items[^1].Id;
        }

        var retainedFrom = settings.HistoryRetention is { } retention ? time.GetUtcNow() - retention : (DateTimeOffset?)null;
        var gap = retainedFrom is { } earliest && from < earliest
            ? $"The provider keeps received emails for {settings.HistoryRetention}; messages received before {earliest:u} may be missing and cannot be recovered."
            : null;
        return new DiscoveryResult(DiscoveryOutcome.Completed, CoverageGap: gap);
    }

    private static InboundDelivery ToDelivery(InboundProviderInstance instance, ResendListedEmail item) => new()
    {
        ProviderInstanceId = instance.Id,
        // A separate, deterministic namespace: a later scan repeats it, a webhook adds its own receipt.
        SourceDeliveryId = $"discovery:{item.Id}",
        SourceMessageId = item.Id,
        ReceivedAt = item.CreatedAt,
        EnvelopeRecipients = item.Recipients,
        RecipientEvidence = "resend_list_api:to,cc,bcc",
        HeaderFrom = item.From,
        Subject = item.Subject,
        TransportKind = TransportKind,
        DeliveryEvidence = DeliveryEvidence,
        RawTransportPayload = item.Raw,
        TransportMetadata = item.Raw,
        ContentReference = item.Id,
    };
}

public enum ResendWebhookStatus
{
    Accepted,
    Duplicate,

    /// <summary>A verified event type that Pairrot does not process; acknowledged, nothing saved.</summary>
    Ignored,

    InvalidSignature,
    MalformedEvent,
    Conflict,
    Unavailable,
}

/// <summary>
/// The Resend delivery adapter: verifies the Svix signature on the raw body, maps a verified
/// <c>email.received</c> event into the common contract with a deferred content reference, and
/// passes it to common intake. HTTP status mapping stays in the endpoint.
/// </summary>
public sealed class ResendWebhookAdapter(IInboundEmailIntake intake, InboundProviderRegistry providers)
{
    public const string TransportKind = "resend_webhook";
    public const string DeliveryEvidence = "svix_signature";
    public const string EventIdHeader = "svix-id";

    /// <summary>
    /// The enabled Resend instance a webhook is for: the named one, or the only one when the URL
    /// names none. <c>null</c> when there is no such instance.
    /// </summary>
    public InboundProviderInstance? FindInstance(string? instanceId)
    {
        var resend = providers.All.Where(i => i.Kind == ResendSettings.Kind && i.Enabled).ToList();
        return instanceId is null
            ? resend.Count == 1 ? resend[0] : null
            : resend.FirstOrDefault(i => i.Id == instanceId);
    }

    public async Task<ResendWebhookStatus> AcceptAsync(
        InboundProviderInstance instance, string body, Func<string, string?> header, CancellationToken ct)
    {
        try
        {
            // The Svix verifier checks the HMAC over id, timestamp, and the unmodified body, and the timestamp tolerance.
            new Webhook(ResendSettings.Of(instance).WebhookSecret).Verify(body, name => header(name!) ?? "");
        }
        catch (Exception ex) when (ex is WebhookVerificationException or FormatException or ArgumentException)
        {
            return ResendWebhookStatus.InvalidSignature;
        }

        var eventId = header(EventIdHeader)!;
        if (eventId.Length > InboundIntake.MaxIdentityLength || ResendPayloads.ParseEvent(body) is not { } parsed)
        {
            return ResendWebhookStatus.MalformedEvent;
        }

        if (parsed.Email is not { } email)
        {
            return ResendWebhookStatus.Ignored;
        }

        var result = await intake.AcceptAsync(new InboundDelivery
        {
            ProviderInstanceId = instance.Id,
            SourceDeliveryId = eventId,
            SourceMessageId = email.EmailId,
            ReceivedAt = email.CreatedAt,
            EnvelopeRecipients = email.Recipients,
            RecipientEvidence = ResendPayloads.RecipientEvidence,
            HeaderFrom = email.From,
            Subject = email.Subject,
            TransportKind = TransportKind,
            DeliveryEvidence = DeliveryEvidence,
            RawTransportPayload = body,
            TransportMetadata = email.Data,
            ContentReference = email.EmailId,
        }, ct);

        return result.Status switch
        {
            IntakeStatus.Accepted => ResendWebhookStatus.Accepted,
            IntakeStatus.Duplicate => ResendWebhookStatus.Duplicate,
            IntakeStatus.Conflict => ResendWebhookStatus.Conflict,
            IntakeStatus.Rejected => ResendWebhookStatus.MalformedEvent,
            _ => ResendWebhookStatus.Unavailable,
        };
    }
}
