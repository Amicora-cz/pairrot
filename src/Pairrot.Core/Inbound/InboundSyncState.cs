namespace Pairrot.Core.Inbound;

/// <summary>
/// Recovery-check progress of one provider instance that supports message discovery. Webhooks
/// remain the normal path; the check lists the provider's received messages to recover deliveries
/// that never reached Pairrot. <see cref="CoveredThrough"/> advances only after a complete scan.
/// </summary>
public class InboundSyncState
{
    /// <summary>Also the target of the instance's discovery jobs.</summary>
    public Guid Id { get; set; }

    public required string ProviderInstanceId { get; set; }

    /// <summary>The earliest reception time to recover; defaults to when the instance was first activated.</summary>
    public DateTimeOffset SyncFrom { get; set; }

    /// <summary>Every message received up to this time is recorded; <c>null</c> before the first complete scan.</summary>
    public DateTimeOffset? CoveredThrough { get; set; }

    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public DateTimeOffset? NextDueAt { get; set; }

    /// <summary>Messages first recorded by discovery (webhooks missed), in total and in the last complete scan.</summary>
    public long DiscoveredCount { get; set; }

    public int LastRunDiscoveredCount { get; set; }

    /// <summary>The latest error, safe to show; cleared by a complete scan.</summary>
    public string? LastErrorCode { get; set; }

    public string? LastErrorMessage { get; set; }

    /// <summary>History the provider could not supply (e.g. beyond its retention); never reported as covered silently.</summary>
    public string? CoverageGap { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
