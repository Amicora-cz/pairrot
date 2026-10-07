namespace Pairrot.Core.Inbound;

/// <summary>
/// One notification address and the bank connection it belongs to. This table is the only record
/// of which tenant owns an address, and of the provider instance the address is bound to. Rows
/// are never deleted or reassigned: a replaced address is retired and stays reserved, so mail
/// still arriving for it can be attributed.
/// </summary>
public class InboundAddress
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid BankConnectionId { get; set; }

    /// <summary>Normalized (lowercase) full address; globally unique, retired ones included.</summary>
    public required string Address { get; set; }

    /// <summary>The configured provider instance that receives mail for the address. Never changes.</summary>
    public required string ProviderInstanceId { get; set; }

    /// <summary>The instance's adapter kind when the address was assigned (<c>stub</c>, <c>resend</c>, ...), for display.</summary>
    public required string ProviderKind { get; set; }

    public InboundEnvironment Environment { get; set; }

    /// <summary>Whether provider-side setup is complete. A reserved address is not usable until it is ready.</summary>
    public InboundProvisioningState ProvisioningState { get; set; }

    /// <summary>The latest setup error, safe to show.</summary>
    public string? ProvisioningError { get; set; }

    public InboundAddressState State { get; set; } = InboundAddressState.Active;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReadyAt { get; set; }
    public DateTimeOffset? RetiredAt { get; set; }

    /// <summary>
    /// Feature 008: whether mail to the address is processed. Separate from <see cref="State"/>:
    /// a paused address still routes to its owner, but nothing it receives is retrieved or parsed.
    /// </summary>
    public InboundAddressProcessing Processing { get; set; } = InboundAddressProcessing.Active;

    public DateTimeOffset? PausedAt { get; set; }

    /// <summary>Paused again soon after a resume: only an operator may resume it.</summary>
    public bool OperatorResumeRequired { get; set; }

    public DateTimeOffset? LastResumedAt { get; set; }
    public int ResumeCount { get; set; }
}

public enum InboundAddressProcessing
{
    Active,

    /// <summary>Too much foreign mail arrived, e.g. from a forwarding rule that forwards everything.</summary>
    PausedForeignMail,
}

/// <summary>Test data stays test data, whichever provider delivered it.</summary>
public enum InboundEnvironment
{
    Test,
    Live,
}

public enum InboundProvisioningState
{
    Pending,
    Ready,
    Failed,
}

public enum InboundAddressState
{
    Active,
    Retired,
}
