using Pairrot.Core.Banking;
using Pairrot.Core.Inbound;

namespace Pairrot.Core.Parsing;

/// <summary>
/// What a bank profile sees: Feature 006's common source snapshot, the receiving system's
/// normalized authentication evidence, and the saved destination connection. Nothing here names
/// a provider, transport, or instance, so equivalent snapshots parse identically whatever
/// delivered them.
/// </summary>
/// <param name="HeaderFrom">The From header as delivered. Its display name proves nothing.</param>
/// <param name="Authentication">Results computed by the receiving system, never by a header inside the email.</param>
/// <param name="Destination">The account of the bank connection that intake routed the message to.</param>
public sealed record BankMessage(
    string? HeaderFrom,
    string? Subject,
    string? Text,
    string? Html,
    SenderAuthentication Authentication,
    InboundSourceKind SourceKind,
    InboundEnvironment Environment,
    DateTimeOffset ReceivedAt,
    CzechBankAccount Destination,
    string Currency);

public enum SourcePart
{
    Text,
    Html,
}

/// <summary>What a recognized notification describes. Only a booked incoming transfer can become a transaction.</summary>
public enum MessageKind
{
    IncomingTransfer,
    OutgoingTransfer,
    BalanceSummary,
    PendingMovement,

    /// <summary>A reversal or correction; never treated as another positive payment.</summary>
    Correction,

    /// <summary>Several movements in one message; unsupported rather than partially converted.</summary>
    MultipleMovements,

    Unrecognized,
}

/// <summary>Field values exactly as a template found them; normalization happens afterwards, the same for every profile.</summary>
public sealed record ExtractedFields
{
    public string? Amount { get; init; }

    /// <summary>The currency as written. A template that states it elsewhere unambiguously fills it in itself.</summary>
    public string? Currency { get; init; }

    public string? VariableSymbol { get; init; }

    /// <summary>The destination account as shown, possibly masked.</summary>
    public string? DestinationAccount { get; init; }

    public string? SenderAccount { get; init; }
    public string? SenderName { get; init; }
    public string? Message { get; init; }
    public string? BookedOn { get; init; }
    public string? OccurredAt { get; init; }

    /// <summary>A bank reference, only where the profile has proven it stable for one movement.</summary>
    public string? BankReference { get; init; }
}

/// <param name="TemplateId">The verified template that matched, e.g. <c>incoming_transfer_v1</c>.</param>
public sealed record TemplateMatch(MessageKind Kind, string? TemplateId = null, ExtractedFields? Fields = null)
{
    public static readonly TemplateMatch Unrecognized = new(MessageKind.Unrecognized);
}

/// <summary>Sender identities established from verified direct-delivery samples. Lowercase, exact.</summary>
public sealed record SenderPolicy(IReadOnlySet<string> Addresses, IReadOnlySet<string> Domains)
{
    /// <summary>No sender is trusted: the profile has no verified sender evidence yet.</summary>
    public static readonly SenderPolicy None = new(new HashSet<string>(), new HashSet<string>());

    public bool IsEmpty => Addresses.Count == 0 && Domains.Count == 0;

    public bool Allows(string address, string domain) => Addresses.Contains(address) || Domains.Contains(domain);
}

/// <summary>How a profile's notifications write amounts and dates.</summary>
/// <param name="GroupSeparators">Thousands separators besides whitespace, e.g. <c>.</c> when <paramref name="DecimalSeparator"/> is <c>,</c>.</param>
/// <param name="CurrencyAliases">Uppercase spellings mapped to ISO codes, e.g. <c>KČ</c> → <c>CZK</c>.</param>
/// <param name="TimeZoneId">The zone of timestamps written without an offset.</param>
public sealed record BankProfileFormats(
    char DecimalSeparator,
    string GroupSeparators,
    string[] DateFormats,
    string[] TimestampFormats,
    string TimeZoneId,
    IReadOnlyDictionary<string, string> CurrencyAliases);

/// <summary>
/// A bank's notification profile, selected by the destination account's bank code and versioned
/// independently. It recognizes templates and extracts raw fields; trust, normalization,
/// validation, and duplicate handling are common and not up to the profile. Tenants cannot
/// supply or choose profiles.
/// </summary>
public interface IBankProfile
{
    /// <summary>Stable profile name, e.g. <c>ceska_sporitelna</c>.</summary>
    string Name { get; }

    /// <summary>The parser version, recorded on every run; bumped whenever extraction changes.</summary>
    string Version { get; }

    /// <summary>The four-digit bank code of accounts this profile handles.</summary>
    string BankCode { get; }

    SenderPolicy Senders { get; }

    /// <summary>Representations with verified templates, preferred first. Empty when none is verified.</summary>
    IReadOnlyList<SourcePart> Parts { get; }

    BankProfileFormats Formats { get; }

    /// <summary>Whether <see cref="ExtractedFields.BankReference"/> is proven to identify one movement.</summary>
    bool HasStableReference { get; }

    /// <summary>Recognizes a template in one representation; HTML arrives already reduced to inert text.</summary>
    TemplateMatch Recognize(string content, SourcePart part, string? subject);

    /// <summary>
    /// Whether a masked destination as the bank prints it is sufficient evidence for the saved
    /// account. The profile must document why; when in doubt, it is not.
    /// </summary>
    bool MaskedDestinationMatches(string masked, CzechBankAccount destination);
}
