using Pairrot.Core.Banking;

namespace Pairrot.Core.Parsing;

/// <summary>
/// Česká spořitelna (bank code 0800), the first selected bank.
/// <para>
/// UNVERIFIED: no real direct-delivery notification samples have been obtained yet, so this
/// profile deliberately trusts no sender, supports no representation, and recognizes no template.
/// Every message is recorded as <c>unsupported</c> (<c>no_verified_templates</c>) and nothing is
/// guessed. Sender identities, subject and body templates, number and date formats, account
/// masking, and any transaction reference must come from sanitized real samples kept as fixtures
/// with their expected extraction (see <c>docs/features/007-bank-message-parsing.md</c>, section 2);
/// only then may this profile gain templates and a new <see cref="Version"/>.
/// </para>
/// </summary>
public sealed class CeskaSporitelnaProfile : IBankProfile
{
    public const string ProfileName = "ceska_sporitelna";

    public string Name => ProfileName;
    public string Version => "0.1.0-unverified";
    public string BankCode => "0800";

    /// <summary>No sender is trusted until real direct-delivery samples establish one.</summary>
    public SenderPolicy Senders => SenderPolicy.None;

    public IReadOnlyList<SourcePart> Parts => [];

    /// <summary>Placeholder Czech formats; not used while no template is verified.</summary>
    public BankProfileFormats Formats { get; } = new(
        ',', ".", [], [], "Europe/Prague", new Dictionary<string, string> { ["KČ"] = "CZK" });

    public bool HasStableReference => false;

    public TemplateMatch Recognize(string content, SourcePart part, string? subject) => TemplateMatch.Unrecognized;

    /// <summary>The bank's masking is unknown, so no masked account is sufficient.</summary>
    public bool MaskedDestinationMatches(string masked, CzechBankAccount destination) => false;
}

/// <summary>The installed bank profiles, one per bank code. Tenants cannot add or choose profiles.</summary>
public sealed class BankProfileRegistry
{
    private readonly Dictionary<string, IBankProfile> _byBankCode;

    public BankProfileRegistry(IEnumerable<IBankProfile> profiles)
    {
        _byBankCode = [];
        foreach (var profile in profiles)
        {
            if (!_byBankCode.TryAdd(profile.BankCode, profile))
            {
                throw new InvalidOperationException($"Bank code {profile.BankCode} has more than one profile.");
            }
        }
    }

    public IReadOnlyCollection<IBankProfile> All => _byBankCode.Values;

    public IBankProfile? For(CzechBankAccount account) => _byBankCode.GetValueOrDefault(account.BankCode);
}
