using Microsoft.EntityFrameworkCore;
using Pairrot.Core.Banking;
using Pairrot.Core.Inbound;
using Pairrot.Core.Tenants;
using Pairrot.Infrastructure.Persistence;

namespace Pairrot.Infrastructure.Inbound;

/// <param name="Address">The matched address when <see cref="State"/> is routed.</param>
public sealed record RoutingDecision(InboundRoutingState State, InboundAddress? Address, InboundHoldReason? Hold);

/// <summary>
/// Resolves an email's owner from the adapter's recipient evidence and the address map only, with
/// each address bound to the provider instance that may deliver it. Subject, body, sender,
/// <c>Message-ID</c>, and forwarding hints are never used to choose a tenant.
/// </summary>
public class InboundRouter(PairrotDbContext db)
{
    public async Task<RoutingDecision> RouteAsync(IReadOnlyCollection<string> recipients, string providerInstanceId, CancellationToken ct)
    {
        if (recipients.Count == 0)
        {
            return new RoutingDecision(InboundRoutingState.MissingRecipientEvidence, null, null);
        }

        var matches = await FindAsync(recipients, ct);
        if (matches.Any(a => a.ProviderInstanceId != providerInstanceId))
        {
            // An address claimed through a provider it is not bound to; never trust the claim.
            return new RoutingDecision(InboundRoutingState.InstanceMismatch, null, null);
        }

        var connections = matches.Select(a => a.BankConnectionId).Distinct().Count();
        if (connections == 0)
        {
            return new RoutingDecision(InboundRoutingState.UnknownRecipient, null, null);
        }

        if (connections > 1)
        {
            // Never fan out one email to several owners.
            return new RoutingDecision(InboundRoutingState.AmbiguousRecipient, null, null);
        }

        // Several aliases of one connection still yield one owner; prefer its current address.
        var address = matches.OrderBy(a => a.State == InboundAddressState.Active ? 0 : 1).ThenByDescending(a => a.CreatedAt).First();
        return new RoutingDecision(InboundRoutingState.Routed, address, await HoldReasonAsync(address, ct));
    }

    /// <summary>
    /// Whether content evidence contradicts a route: its recipients must include the routed address
    /// and no other connection's address. Content without recipient evidence contradicts nothing.
    /// </summary>
    public async Task<bool> ContradictsRouteAsync(InboundContentSnapshot content, InboundAddress routed, CancellationToken ct)
    {
        if (content.DeliveredRecipients is not { } delivered)
        {
            return false;
        }

        var known = await FindAsync(EmailAddresses.NormalizeAll(delivered), ct);
        return known.All(a => a.Id != routed.Id) || known.Any(a => a.BankConnectionId != routed.BankConnectionId);
    }

    private async Task<List<InboundAddress>> FindAsync(IReadOnlyCollection<string> recipients, CancellationToken ct)
    {
        var addresses = recipients.ToList();
        return await db.InboundAddresses.AsNoTracking().Where(a => addresses.Contains(a.Address)).ToListAsync(ct);
    }

    /// <summary>
    /// Why a correctly routed message must not be processed further: the message stays bound to
    /// its owner for history, but bank processing waits.
    /// </summary>
    public async Task<InboundHoldReason?> HoldReasonAsync(InboundAddress address, CancellationToken ct)
    {
        var owner = await db.Tenants.AsNoTracking()
            .Where(t => t.Id == address.TenantId)
            .Select(t => new { t.Status, ParentStatus = t.ParentTenant != null ? t.ParentTenant.Status : (TenantStatus?)null })
            .SingleAsync(ct);
        if (owner.Status != TenantStatus.Active || owner.ParentStatus is { } parent && parent != TenantStatus.Active)
        {
            return InboundHoldReason.TenantNotActive;
        }

        var connectionStatus = await db.BankConnections.AsNoTracking()
            .Where(b => b.Id == address.BankConnectionId)
            .Select(b => b.Status)
            .SingleAsync(ct);
        if (connectionStatus == BankConnectionStatus.Archived)
        {
            return InboundHoldReason.BankConnectionArchived;
        }

        return address.State == InboundAddressState.Retired ? InboundHoldReason.AddressRetired : null;
    }
}
