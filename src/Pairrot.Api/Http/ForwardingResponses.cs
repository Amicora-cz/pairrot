using Pairrot.Core.Inbound;
using Pairrot.Infrastructure.Inbound;

namespace Pairrot.Api.Http;

/// <summary>A forwarding mailbox entered by the tenant; active from registration (MVP, no verification).</summary>
/// <param name="Address"><c>null</c> once erased with the tenant (Feature 014).</param>
public sealed record ForwardingSourceResponse(Guid Id, string? Address, string State, DateTimeOffset CreatedAt, DateTimeOffset? ErasedAt)
{
    public static ForwardingSourceResponse From(ForwardingSource s) =>
        new(s.Id, s.Address, InboundMessageResponse.Snake(s.State), s.CreatedAt, s.ErasedAt);
}

/// <summary>Whether forwarding works, from received mail (spec 008, section 6). Foreign mail appears only as counts and domains.</summary>
public sealed record ReceptionStatusResponse(
    string SetupStatus,
    string? InboundAddress,
    string? AddressProcessing,
    DateTimeOffset? PausedAt,
    bool OperatorResumeRequired,
    int ForeignMessagesInWindow,
    IReadOnlyList<string> RecentForeignDomains,
    ConfirmationResponse? Confirmation,
    LatestBankMessageResponse? LatestBankMessage,
    IReadOnlyList<ForwardingSourceResponse> ForwardingSources)
{
    public static ReceptionStatusResponse From(ReceptionStatus s) => new(
        s.SetupStatus,
        s.Address?.ProvisioningState == InboundProvisioningState.Ready ? s.Address.Address : null,
        s.Address is { } address ? InboundMessageResponse.Snake(address.Processing) : null,
        s.Address?.PausedAt,
        s.Address?.OperatorResumeRequired ?? false,
        s.ForeignMessagesInWindow,
        s.RecentForeignDomains,
        s.Confirmation is { } c ? new ConfirmationResponse(c.MessageId, c.ReceivedAt, c.SenderDomain, c.Code, c.Excerpt) : null,
        s.LatestBankMessage is { } b
            ? new LatestBankMessageResponse(b.MessageId, b.ReceivedAt, b.Outcome, b.TrustDecision, b.OriginPath, b.ForwardingSource)
            : null,
        s.Sources.Select(ForwardingSourceResponse.From).ToList());
}

/// <param name="Excerpt">The confirmation's text, to be shown escaped. Links are never followed by Pairrot.</param>
public sealed record ConfirmationResponse(Guid MessageId, DateTimeOffset ReceivedAt, string? SenderDomain, string? Code, string Excerpt);

public sealed record LatestBankMessageResponse(
    Guid MessageId, DateTimeOffset ReceivedAt, string? ParseOutcome, string? TrustDecision, string? OriginPath, string? ForwardingSource);
