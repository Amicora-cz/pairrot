using System.Text;
using Pairrot.Api.Http;
using Pairrot.Infrastructure.Inbound.Resend;

namespace Pairrot.Api.Endpoints;

/// <summary>
/// Provider webhooks. They are authenticated by the provider's signature only: no cookie, no API
/// key, and therefore no antiforgery token, but never without signature verification. There is
/// no generic endpoint that accepts a caller's claim that an email is trusted.
/// </summary>
public static class WebhookEndpoints
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static void MapWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        // With one Resend instance the instance may be omitted; with several, each has its own URL.
        app.MapPost("/v1/webhooks/resend", (HttpContext http, ResendWebhookAdapter resend, CancellationToken ct) =>
            ResendAsync(null, http, resend, ct)).AllowAnonymous();
        app.MapPost("/v1/webhooks/resend/{instanceId}", (string instanceId, HttpContext http, ResendWebhookAdapter resend, CancellationToken ct) =>
            ResendAsync(instanceId, http, resend, ct)).AllowAnonymous();
    }

    private static async Task<IResult> ResendAsync(string? instanceId, HttpContext http, ResendWebhookAdapter resend, CancellationToken ct)
    {
        if (resend.FindInstance(instanceId) is not { } instance)
        {
            // No such enabled instance (e.g. the stub-only default): the endpoint does not exist here.
            return ApiErrors.NotFound();
        }

        var limit = ResendSettings.Of(instance).MaxWebhookBytes;
        var body = await ReadLimitedAsync(http.Request, limit, ct);
        if (body is null)
        {
            return ApiErrors.Result(StatusCodes.Status413PayloadTooLarge, "payload_too_large", $"The webhook body exceeds {limit} bytes.");
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(body);
        }
        catch (DecoderFallbackException)
        {
            return ApiErrors.Result(StatusCodes.Status400BadRequest, "invalid_signature", "The webhook signature is invalid.");
        }

        var status = await resend.AcceptAsync(
            instance, text, name => http.Request.Headers.TryGetValue(name, out var value) ? value.ToString() : null, ct);
        return status switch
        {
            ResendWebhookStatus.Accepted or ResendWebhookStatus.Duplicate or ResendWebhookStatus.Ignored =>
                Results.Ok(new { status = status.ToString().ToLowerInvariant() }),
            ResendWebhookStatus.InvalidSignature => ApiErrors.Result(StatusCodes.Status400BadRequest, "invalid_signature",
                "The webhook signature or timestamp is invalid."),
            ResendWebhookStatus.MalformedEvent => ApiErrors.Result(StatusCodes.Status400BadRequest, "invalid_event",
                "The event is missing required data."),
            ResendWebhookStatus.Conflict => ApiErrors.Result(StatusCodes.Status409Conflict, "event_conflict",
                "This event ID was already received with different content."),
            _ => ApiErrors.Result(StatusCodes.Status503ServiceUnavailable, "temporarily_unavailable",
                "The event could not be saved; retry later."),
        };
    }

    /// <summary>The body, or <c>null</c> when it is longer than <paramref name="limit"/>. Never buffers more than the limit.</summary>
    private static async Task<byte[]?> ReadLimitedAsync(HttpRequest request, int limit, CancellationToken ct)
    {
        if (request.ContentLength > limit)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[16384];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
