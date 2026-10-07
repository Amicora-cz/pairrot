namespace Pairrot.Core.Inbound;

/// <summary>
/// Feature 008: a mailbox of the tenant that forwards bank notifications to a connection's
/// address. In the MVP the tenant enters it by hand and it is active at once; Pairrot does not
/// verify that the tenant controls it. Its address alone is never evidence: forwarded mail is
/// valid only with receiving-system authentication of this mailbox.
/// </summary>
public class ForwardingSource
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid BankConnectionId { get; set; }

    /// <summary>Normalized lowercase address.</summary>
    public required string Address { get; set; }

    public ForwardingSourceState State { get; set; } = ForwardingSourceState.Active;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? RemovedAt { get; set; }
}

public enum ForwardingSourceState
{
    Active,
    Removed,
}
