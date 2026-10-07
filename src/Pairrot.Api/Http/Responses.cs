using System.Text.Json;
using System.Text.Json.Serialization;
using Pairrot.Core.Banking;
using Pairrot.Core.Inbound;
using Pairrot.Core.Jobs;
using Pairrot.Core.Payments;
using Pairrot.Core.Tenants;
using Pairrot.Infrastructure.Banking;
using Pairrot.Infrastructure.Payments;
using Pairrot.Infrastructure.Tenants;
using Pairrot.Infrastructure.Users;

namespace Pairrot.Api.Http;

// Serialized as snake_case JSON (see Program.cs). Key secrets appear only in IssuedApiKeyResponse.

public sealed record CapabilitiesResponse(bool CanCreateSubtenants, bool CanCreateCampaigns);

public sealed record ConfigurationResponse(string Name, string? ContactEmail)
{
    public static ConfigurationResponse From(Tenant tenant) => new(tenant.Name, tenant.ContactEmail);
}

public sealed record TenantResponse(
    Guid Id,
    Guid? ParentTenantId,
    string ExternalId,
    TenantStatus Status,
    ConfigurationResponse Configuration,
    CapabilitiesResponse Capabilities,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? DeletedAt)
{
    public static TenantResponse From(Tenant tenant) => new(
        tenant.Id,
        tenant.ParentTenantId,
        tenant.ExternalId,
        tenant.Status,
        ConfigurationResponse.From(tenant),
        new CapabilitiesResponse(tenant.CanCreateSubtenants, tenant.CanCreateCampaigns),
        tenant.CreatedAt,
        tenant.UpdatedAt,
        tenant.DeletedAt);
}

public sealed record ApiKeyResponse(
    Guid Id, string? Label, ApiKeyPurpose Purpose, DateTimeOffset CreatedAt, DateTimeOffset? RevokedAt)
{
    public static ApiKeyResponse From(TenantApiKey key) => new(key.Id, key.Label, key.Purpose, key.CreatedAt, key.RevokedAt);
}

/// <summary>Returned only when a key is created; the secret cannot be retrieved again.</summary>
public sealed record IssuedApiKeyResponse(
    Guid Id, string? Label, ApiKeyPurpose Purpose, DateTimeOffset CreatedAt, string Secret)
{
    public static IssuedApiKeyResponse From(IssuedApiKey issued) =>
        new(issued.Key.Id, issued.Key.Label, issued.Key.Purpose, issued.Key.CreatedAt, issued.Secret);
}

public sealed record CreatedSubtenantResponse(TenantResponse Tenant, IssuedApiKeyResponse ApiKey);

public sealed record ListResponse<T>(IReadOnlyList<T> Items, Guid? NextCursor);

public sealed record AntiforgeryResponse(string Token, string HeaderName);

public sealed record UserResponse(Guid Id, string Email);

public sealed record SessionInfoResponse(
    DateTimeOffset CreatedAt, DateTimeOffset LastActivityAt, DateTimeOffset IdleExpiresAt, DateTimeOffset AbsoluteExpiresAt);

/// <param name="Blocked">The tenant or its parent is suspended; operations for it are refused.</param>
public sealed record MembershipResponse(string Role, bool Blocked, TenantResponse Tenant);

/// <summary>The signed-in user and their access. Never contains the cookie or any secret.</summary>
public sealed record SessionSummaryResponse(
    UserResponse User,
    SessionInfoResponse Session,
    Guid? SelectedTenantId,
    IReadOnlyList<MembershipResponse> Memberships)
{
    public static SessionSummaryResponse From(SessionSummary summary)
    {
        var session = summary.Current.Session;
        return new SessionSummaryResponse(
            new UserResponse(session.UserId, summary.Current.Email),
            new SessionInfoResponse(session.CreatedAt, session.LastActivityAt, session.IdleExpiresAt, session.AbsoluteExpiresAt),
            summary.Current.Tenant?.Id,
            summary.Memberships.Select(m => new MembershipResponse(m.Membership.Role, m.Blocked, TenantResponse.From(m.Tenant))).ToList());
    }
}

/// <param name="Type"><c>user</c>, <c>api_key</c>, or <c>administrator</c>.</param>
public sealed record AuditActorResponse(string Type, Guid? UserId, string? Email, Guid? ApiKeyId, string? Description);

public sealed record AuditEventResponse(
    Guid Id, Guid TenantId, string Action, AuditActorResponse Actor, JsonElement Details, DateTimeOffset CreatedAt)
{
    public static AuditEventResponse From(AuditEventView view)
    {
        var e = view.Event;
        var actor = e switch
        {
            { ActorUserId: { } userId } => new AuditActorResponse("user", userId, view.ActorUserEmail, null, null),
            { ActorApiKeyId: { } keyId } => new AuditActorResponse("api_key", null, null, keyId, null),
            _ => new AuditActorResponse("administrator", null, null, null, e.ActorDescription),
        };

        return new AuditEventResponse(
            e.Id, e.TargetTenantId, e.Action, actor, JsonSerializer.Deserialize<JsonElement>(e.Details), e.CreatedAt);
    }
}

/// <param name="EmailProvider">
/// The provider kind of the current address (<c>stub</c>, <c>resend</c>, ...), or of the default
/// instance before assignment. <c>stub</c> means a local test address: no email is ever delivered.
/// </param>
/// <param name="AddressStatus">
/// <c>not_created</c>, <c>pending</c> or <c>failed</c> (provider setup of a reserved address),
/// <c>stub_created</c> (test address), or <c>live_created</c>. No state claims that a bank email arrived.
/// </param>
/// <param name="InboundAddress">Shown only once the address is usable.</param>
/// <param name="AddressReplaced">Address assignment only: <c>true</c> when a test address was retired for a new live one.</param>
public sealed record BankConnectionResponse(
    Guid Id,
    Guid TenantId,
    string? Label,
    string AccountNumber,
    string Iban,
    string Currency,
    BankConnectionStatus Status,
    string EmailProvider,
    string? ProviderInstanceId,
    string AddressStatus,
    string? InboundAddress,
    DateTimeOffset? AddressCreatedAt,
    string? ProvisioningError,
    ReceptionResponse Reception,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? AddressReplaced = null)
{
    public static BankConnectionResponse From(BankConnectionView view, string providerName)
    {
        var (c, address) = (view.Connection, view.Address);
        var ready = address?.ProvisioningState == InboundProvisioningState.Ready;
        return new BankConnectionResponse(
            c.Id,
            c.TenantId,
            c.Label,
            c.AccountNumber,
            c.Iban,
            c.Currency,
            c.Status,
            address?.ProviderKind ?? providerName,
            address?.ProviderInstanceId,
            address switch
            {
                null => "not_created",
                { ProvisioningState: InboundProvisioningState.Pending } => "pending",
                { ProvisioningState: InboundProvisioningState.Failed } => "failed",
                { Environment: InboundEnvironment.Test } => "stub_created",
                _ => "live_created",
            },
            ready ? address!.Address : null,
            ready ? address!.ReadyAt : null,
            address?.ProvisioningError,
            ReceptionResponse.From(view.Reception),
            c.CreatedAt,
            c.UpdatedAt,
            c.ArchivedAt,
            view.ReplacedStub);
    }
}

/// <summary>Separate reception facts; none of them proves that a bank sent an email or that a payment arrived.</summary>
public sealed record ReceptionResponse(
    DateTimeOffset? LastMessageReceivedAt, DateTimeOffset? LastContentStoredAt, ErrorAtResponse? LastContentError)
{
    public static ReceptionResponse From(ReceptionFacts facts) => new(
        facts.LastMessageReceivedAt,
        facts.LastContentRetrievedAt,
        facts.LastRetrievalErrorCode is { } code ? new ErrorAtResponse(code, facts.LastRetrievalErrorMessage ?? "", facts.LastRetrievalErrorAt) : null);
}

public sealed record ErrorAtResponse(string Code, string Message, DateTimeOffset? At);

/// <summary>Normalized SPF/DKIM/DMARC results from the receiving system; <c>unknown</c> when there is no evidence.</summary>
public sealed record SenderAuthenticationResponse(string Spf, string Dkim, string Dmarc, string? Origin);

/// <param name="SourceAvailable">Whether <c>GET .../source</c> returns content.</param>
/// <param name="Retrievals">Detail only: every retrieval job of the message, oldest first; none for inline content.</param>
public sealed record InboundMessageResponse(
    Guid Id,
    Guid TenantId,
    Guid BankConnectionId,
    string ProviderInstanceId,
    string TransportKind,
    string SourceKind,
    string Environment,
    string RoutingState,
    string? HoldReason,
    string ContentMode,
    string ContentState,
    string? HeaderFrom,
    string? EnvelopeSender,
    string? Subject,
    IReadOnlyList<string> EnvelopeRecipients,
    string RecipientEvidence,
    DateTimeOffset ReceivedAt,
    DateTimeOffset? ContentStoredAt,
    string? ContentSha256,
    SenderAuthenticationResponse SenderAuthentication,
    ErrorAtResponse? ContentError,
    bool SourceAvailable,
    Guid? LatestParseRunId,
    string? LatestParseOutcome,
    DateTimeOffset? ParsedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<RetrievalJobResponse>? Retrievals = null)
{
    public static InboundMessageResponse From(InboundMessage m, IReadOnlyList<BackgroundJob>? jobs = null) => new(
        m.Id,
        m.TenantId!.Value,
        m.BankConnectionId!.Value,
        m.ProviderInstanceId,
        m.TransportKind,
        Snake(m.SourceKind),
        Snake(m.Environment),
        Snake(m.RoutingState),
        m.HoldReason is { } hold ? Snake(hold) : null,
        Snake(m.ContentMode),
        Snake(m.ContentState),
        m.HeaderFrom,
        m.EnvelopeSender,
        m.Subject,
        m.EnvelopeRecipients,
        m.RecipientEvidence,
        m.ReceivedAt,
        m.ContentStoredAt,
        m.ContentSha256,
        new SenderAuthenticationResponse(m.SpfResult, m.DkimResult, m.DmarcResult, m.AuthenticationOrigin),
        m.FetchErrorCode is { } code ? new ErrorAtResponse(code, m.FetchErrorMessage ?? "", m.FetchErrorAt) : null,
        m.ContentState == InboundContentState.Available && m.HoldReason != InboundHoldReason.RecipientMismatch,
        m.LatestParseRunId,
        m.LatestParseOutcome is { } outcome ? Snake(outcome) : null,
        m.ParsedAt,
        m.CreatedAt,
        m.UpdatedAt,
        jobs?.Select(RetrievalJobResponse.From).ToList());

    internal static string Snake<TEnum>(TEnum value) where TEnum : struct, Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());
}

/// <summary>A retrieval attempt series. Lease details are internal and never returned.</summary>
public sealed record RetrievalJobResponse(
    Guid Id,
    int Generation,
    string State,
    int AttemptCount,
    DateTimeOffset? NextAttemptAt,
    ErrorAtResponse? LastError,
    string RequestedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt)
{
    public static RetrievalJobResponse From(BackgroundJob job) => new(
        job.Id,
        job.Generation,
        InboundMessageResponse.Snake(job.State),
        job.AttemptCount,
        job.State is BackgroundJobState.Queued or BackgroundJobState.RetryWait ? job.DueAt : null,
        job.LastErrorCode is { } code ? new ErrorAtResponse(code, job.LastErrorMessage ?? "", null) : null,
        job.RequestedBy,
        job.CreatedAt,
        job.CompletedAt);
}

/// <summary>
/// The stored common source for display. Email HTML is returned as a string to be shown escaped,
/// never rendered. The provider's raw content is not returned: it may contain signed URLs.
/// </summary>
public sealed record InboundMessageSourceResponse(
    Guid MessageId,
    string SourceKind,
    string Environment,
    string? HeaderFrom,
    string? Subject,
    IReadOnlyList<string> EnvelopeRecipients,
    IReadOnlyList<string>? ContentRecipients,
    DateTimeOffset ReceivedAt,
    DateTimeOffset ContentStoredAt,
    string ContentSha256,
    string? Text,
    string? Html,
    JsonElement? Headers,
    SenderAuthenticationResponse SenderAuthentication,
    JsonElement? AuthenticationRaw,
    JsonElement? Attachments)
{
    public static InboundMessageSourceResponse From(InboundMessage m) => new(
        m.Id,
        InboundMessageResponse.Snake(m.SourceKind),
        InboundMessageResponse.Snake(m.Environment),
        m.HeaderFrom,
        m.Subject,
        m.EnvelopeRecipients,
        m.ContentRecipients,
        m.ReceivedAt,
        m.ContentStoredAt!.Value,
        m.ContentSha256!,
        m.TextBody,
        m.HtmlBody,
        Json(m.Headers),
        new SenderAuthenticationResponse(m.SpfResult, m.DkimResult, m.DmarcResult, m.AuthenticationOrigin),
        Json(m.AuthenticationRaw),
        Json(m.Attachments));

    private static JsonElement? Json(string? value) => value is null ? null : JsonSerializer.Deserialize<JsonElement>(value);
}

/// <summary>The retrieval queued by <c>retry-fetch</c>, or the one already queued.</summary>
public sealed record QueuedRetrievalResponse(Guid JobId, string State, bool Created);

/// <summary>
/// Payment data for a QR code. The summary fields and <see cref="Payload"/> come from the same
/// validated values; <c>format</c> is always <c>SPAYD</c> (Czech QR Platba).
/// </summary>
public sealed record PaymentQrResponse(
    Guid BankConnectionId,
    Guid TenantId,
    string AccountNumber,
    string Iban,
    long Amount,
    string Currency,
    string? VariableSymbol,
    string? Message,
    string Format,
    string Payload)
{
    public static PaymentQrResponse From(PaymentQr qr) => new(
        qr.Connection.Id,
        qr.Connection.TenantId,
        qr.Connection.AccountNumber,
        qr.Payment.Iban,
        qr.Payment.Amount,
        qr.Payment.Currency,
        qr.Payment.VariableSymbol,
        qr.Payment.Message,
        SpaydPayment.FormatName,
        qr.Payment.Payload);
}

/// <summary>
/// An expected payment. <c>paid_amount</c> is always 0 until payments are matched (a later
/// feature); <c>remaining_amount</c> is <c>null</c> for a cancelled request, which asks for nothing.
/// </summary>
/// <param name="Replayed">Creation only: <c>true</c> when an earlier identical creation was returned.</param>
/// <param name="PaymentInstructions">Creation only, and only while the request is payable.</param>
public sealed record PaymentRequestResponse(
    Guid Id,
    Guid TenantId,
    Guid BankConnectionId,
    string ExternalId,
    string AccountNumber,
    string Iban,
    long Amount,
    string Currency,
    string VariableSymbol,
    string? Message,
    string? Description,
    DateTimeOffset? DueAt,
    PaymentRequestStatus Status,
    bool IsOverdue,
    long PaidAmount,
    long? RemainingAmount,
    string? CancellationReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CancelledAt,
    int Version,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Replayed = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PaymentInstructionsResponse? PaymentInstructions = null)
{
    public static PaymentRequestResponse From(PaymentRequest p, DateTimeOffset now) => new(
        p.Id,
        p.TenantId,
        p.BankConnectionId,
        p.ExternalId,
        p.AccountNumber,
        p.Iban,
        p.Amount,
        p.Currency,
        p.VariableSymbol,
        p.Message,
        p.Description,
        p.DueAt,
        p.Status,
        PaymentRequestRules.IsOverdue(p, now),
        0,
        p.Status == PaymentRequestStatus.Open ? p.Amount : null,
        p.CancellationReason,
        p.CreatedAt,
        p.UpdatedAt,
        p.CancelledAt,
        p.Version);
}

/// <summary>The payment instructions saved when the request was created; <c>payload</c> is never regenerated.</summary>
public sealed record PaymentInstructionsResponse(
    Guid PaymentRequestId,
    Guid BankConnectionId,
    Guid TenantId,
    string AccountNumber,
    string Iban,
    long Amount,
    string Currency,
    string VariableSymbol,
    string? Message,
    string Format,
    string GeneratorVersion,
    string Payload)
{
    public static PaymentInstructionsResponse From(PaymentRequest p) => new(
        p.Id,
        p.BankConnectionId,
        p.TenantId,
        p.AccountNumber,
        p.Iban,
        p.Amount,
        p.Currency,
        p.VariableSymbol,
        p.Message,
        p.QrFormat,
        p.QrGeneratorVersion,
        p.QrPayload);
}
