using System.Text.Json;
using Pairrot.Core.Webhooks;
using Pairrot.Infrastructure.Webhooks;

namespace Pairrot.Api.Http;

// Features 010 and 011. Signing secrets appear only in SignedEndpointResponse, once.

/// <param name="State"><c>current</c>, <c>retiring</c> (still signs until <c>retires_at</c>), or <c>retired</c>.</param>
public sealed record WebhookKeyResponse(string KeyId, string State, DateTimeOffset CreatedAt, DateTimeOffset? RetiresAt, DateTimeOffset? RetiredAt)
{
    public static WebhookKeyResponse From(WebhookSigningKey k) =>
        new(k.KeyId, InboundMessageResponse.Snake(k.State), k.CreatedAt, k.RetiresAt, k.RetiredAt);
}

public sealed record WebhookCountsResponse(int Pending, int RetryWait, int Running, int Failed, int Held, int Delivered, int Abandoned);

/// <summary>
/// Whether the endpoint works, from evidence rather than configuration: a saved URL is only
/// <c>configured</c>; <c>test_accepted</c> and <c>delivering</c> need accepted requests.
/// </summary>
/// <param name="Status"><c>paused</c>, <c>configured</c>, <c>test_accepted</c>, <c>delivering</c>, or <c>failing</c>.</param>
public sealed record WebhookHealthResponse(string Status, DateTimeOffset? LastTestAcceptedAt, DateTimeOffset? LastSuccessAt, int CurrentFailures);

/// <param name="Version">The ETag for <c>PATCH</c> and <c>expected_version</c> for rotation.</param>
public sealed record WebhookEndpointResponse(
    Guid Id,
    Guid TenantId,
    string Url,
    string Scope,
    string State,
    string Environment,
    int DestinationRevision,
    IReadOnlyList<WebhookKeyResponse> SigningKeys,
    WebhookCountsResponse Deliveries,
    WebhookHealthResponse Health,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int Version)
{
    public static WebhookEndpointResponse From(EndpointView view)
    {
        var e = view.Endpoint;
        var c = view.Counts;
        var failures = c.Failed + c.Held;
        var status = e.State == WebhookEndpointState.Paused ? "paused"
            : failures > 0 || c.RetryWait > 0 ? "failing"
            : e.LastSuccessAt is not null ? "delivering"
            : e.LastTestAcceptedAt is not null ? "test_accepted"
            : "configured";
        return new WebhookEndpointResponse(
            e.Id,
            e.TenantId,
            e.Url,
            InboundMessageResponse.Snake(e.Scope),
            InboundMessageResponse.Snake(e.State),
            InboundMessageResponse.Snake(e.Environment),
            e.DestinationRevision,
            view.Keys.Select(WebhookKeyResponse.From).ToList(),
            new WebhookCountsResponse(c.Pending, c.RetryWait, c.Running, c.Failed, c.Held, c.Delivered, c.Abandoned),
            new WebhookHealthResponse(status, e.LastTestAcceptedAt, e.LastSuccessAt, failures + c.RetryWait),
            e.CreatedAt,
            e.UpdatedAt,
            e.Version);
    }
}

/// <param name="Secret"><c>pwhsec_…</c>; shown once, never again. Retries of the same command return the endpoint without it.</param>
public sealed record SigningSecretResponse(string KeyId, string Secret);

public sealed record SignedEndpointResponse(WebhookEndpointResponse Endpoint, SigningSecretResponse SigningSecret)
{
    public static SignedEndpointResponse From(SecretReveal reveal) =>
        new(WebhookEndpointResponse.From(reveal.Endpoint), new SigningSecretResponse(reveal.KeyId, reveal.Secret));
}

/// <param name="Body">The exact JSON sent to receivers.</param>
public sealed record WebhookEventResponse(
    Guid Id,
    string Type,
    Guid TenantId,
    Guid? PaymentRequestId,
    int? Sequence,
    string Environment,
    bool Synthetic,
    DateTimeOffset CreatedAt,
    JsonElement Body)
{
    public static WebhookEventResponse From(WebhookEvent e) => new(
        e.Id, e.Type, e.TenantId, e.PaymentRequestId, e.Sequence, InboundMessageResponse.Snake(e.Environment), e.Synthetic, e.CreatedAt,
        JsonDocument.Parse(e.Body).RootElement.Clone());
}

/// <param name="State"><c>pending</c>, <c>running</c>, <c>retry_wait</c>, <c>delivered</c>, <c>failed</c>, <c>held</c>, or <c>abandoned</c>.</param>
/// <param name="AttemptInGeneration">Attempts used of <c>max_attempts</c> in the current generation.</param>
/// <param name="NextAttemptAt">When the next automatic attempt may start; <c>null</c> when none is scheduled.</param>
public sealed record WebhookDeliveryResponse(
    Guid Id,
    Guid EventId,
    Guid EndpointId,
    Guid OwnerTenantId,
    int DestinationRevision,
    string State,
    string? HoldReason,
    int Generation,
    int AttemptInGeneration,
    int MaxAttempts,
    int TotalAttempts,
    DateTimeOffset? NextAttemptAt,
    string? LastOutcome,
    string? LastErrorCode,
    string? LastErrorMessage,
    int? LastHttpStatus,
    string? AbandonReason,
    DateTimeOffset? DeliveredAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int Version)
{
    public static WebhookDeliveryResponse From(WebhookDelivery d) => new(
        d.Id,
        d.EventId,
        d.EndpointId,
        d.OwnerTenantId,
        d.DestinationRevision,
        InboundMessageResponse.Snake(d.State),
        d.HoldReason,
        d.Generation,
        d.AttemptInGeneration,
        WebhookRetryPolicy.MaxAttempts,
        d.TotalAttempts,
        d.State is WebhookDeliveryState.Pending or WebhookDeliveryState.RetryWait ? d.DueAt : null,
        d.LastOutcome,
        d.LastErrorCode,
        d.LastErrorMessage,
        d.LastHttpStatus,
        d.AbandonReason,
        d.DeliveredAt,
        d.CreatedAt,
        d.UpdatedAt,
        d.Version);
}

/// <summary>One attempt; no headers or secrets, only the key IDs that signed it.</summary>
public sealed record WebhookAttemptResponse(
    Guid Id,
    int Generation,
    int AttemptNumber,
    int DestinationRevision,
    string Url,
    string KeyIds,
    string Outcome,
    int? HttpStatus,
    string? ErrorCode,
    string? ResponseExcerpt,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    int? DurationMs)
{
    public static WebhookAttemptResponse From(WebhookDeliveryAttempt a) => new(
        a.Id, a.Generation, a.AttemptNumber, a.DestinationRevision, a.Url, a.KeyIds, InboundMessageResponse.Snake(a.Outcome),
        a.HttpStatus, a.ErrorCode, a.ResponseExcerpt, a.StartedAt, a.CompletedAt, a.DurationMs);
}

public sealed record WebhookDeliveryDetailResponse(
    WebhookDeliveryResponse Delivery, WebhookEventResponse Event, IReadOnlyList<WebhookAttemptResponse> Attempts, string CurrentUrl)
{
    public static WebhookDeliveryDetailResponse From(DeliveryView view) => new(
        WebhookDeliveryResponse.From(view.Delivery),
        WebhookEventResponse.From(view.Event),
        view.Attempts.Select(WebhookAttemptResponse.From).ToList(),
        view.CurrentUrl);
}

public sealed record QueuedDeliveriesResponse(IReadOnlyList<WebhookDeliveryResponse> Deliveries);
