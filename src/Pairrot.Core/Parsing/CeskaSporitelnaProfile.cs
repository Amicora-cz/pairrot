using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Pairrot.Core.Banking;

namespace Pairrot.Core.Parsing;

/// <summary>
/// Česká spořitelna (bank code 0800), the George "Informační zprávy → Příchozí platby" e-mail.
/// <para>
/// PROVISIONAL, NOT VERIFIED AGAINST A REAL SAMPLE. Built only from public descriptions:
/// the sender <c>ceskasporitelna@csas.cz</c> and subject <c>Přišla platba</c> (Redbit's setup guide),
/// and the opening sentence "na účet … právě dorazila platba ve výši … Kč", followed by an
/// "Informace o transakci" block (the bank's own answer on the FinExpert forum, 2024-11-15).
/// The exact field labels, date format, account masking, and HTML layout are not public, so:
/// </para>
/// <list type="bullet">
/// <item>Amount and destination account come only from the labeled opening sentence, never from the
/// first number or the balance line.</item>
/// <item>Other fields are read from lines whose label matches a short list of likely Czech labels;
/// a missing booking date, unknown account form, or any doubt makes the message <c>review_required</c>
/// with the candidate kept, never a guessed value.</item>
/// <item>Trust still needs a <c>csas.cz</c> From address with DMARC <c>pass</c> from the receiving
/// system. Forwarded copies (e.g. from Gmail) are therefore never trusted.</item>
/// <item>No stable bank reference is assumed, so lookalike payments are flagged, not merged.</item>
/// </list>
/// <para>
/// Once a real direct-delivery sample has been received, replace this with its sanitized fixture
/// and exact templates, and raise <see cref="Version"/> to a verified one (Feature 7, criteria 1 and 12).
/// </para>
/// </summary>
public sealed partial class CeskaSporitelnaProfile : IBankProfile
{
    public const string ProfileName = "ceska_sporitelna";

    public string Name => ProfileName;
    public string Version => "0.2.0-provisional";
    public string BankCode => "0800";

    /// <summary>The bank's own domain; DMARC <c>pass</c> ties the From address to it.</summary>
    public SenderPolicy Senders { get; } = new(new HashSet<string> { "ceskasporitelna@csas.cz" }, new HashSet<string> { "csas.cz" });

    public IReadOnlyList<SourcePart> Parts => [SourcePart.Text, SourcePart.Html];

    public BankProfileFormats Formats { get; } = new(
        ',', ".", ["d.M.yyyy"], [], "Europe/Prague", new Dictionary<string, string> { ["KČ"] = "CZK", ["CZK"] = "CZK" });

    public bool HasStableReference => false;

    public TemplateMatch Recognize(string content, SourcePart part, string? subject)
    {
        var text = content.Replace(' ', ' ');
        var incoming = IncomingSentence().Match(text);
        if (!incoming.Success)
        {
            return OutgoingHint().IsMatch(text) || subject?.Contains("Odešla platba", StringComparison.OrdinalIgnoreCase) == true
                ? new TemplateMatch(MessageKind.OutgoingTransfer, "george_outgoing_provisional")
                : TemplateMatch.Unrecognized;
        }

        var labeled = Labeled(text);
        string? Field(params string[] labels) => labels.Select(l => labeled.GetValueOrDefault(Fold(l))).FirstOrDefault(v => v is not null);

        var booked = Field("Datum zaúčtování", "Zaúčtováno", "Datum transakce", "Datum");
        return new TemplateMatch(MessageKind.IncomingTransfer, "george_incoming_provisional", new ExtractedFields
        {
            Amount = incoming.Groups["amount"].Value.Trim(),
            Currency = incoming.Groups["currency"].Value,
            DestinationAccount = incoming.Groups["account"].Value.Trim(),
            VariableSymbol = Field("Variabilní symbol", "VS"),
            SenderAccount = Field("Z účtu", "Protiúčet", "Číslo protiúčtu", "Účet plátce", "Účet odesílatele"),
            SenderName = Field("Název protiúčtu", "Plátce", "Jméno plátce", "Odesílatel", "Od"),
            Message = Field("Zpráva pro příjemce", "Zpráva"),
            BookedOn = booked is null ? null : DateText().Match(booked) is { Success: true } date
                ? Regex.Replace(date.Value, @"\s", "")
                : booked,
        });
    }

    /// <summary>The bank's masking is unknown, so no masked account is sufficient.</summary>
    public bool MaskedDestinationMatches(string masked, CzechBankAccount destination) => false;

    /// <summary>
    /// <c>Label: value</c>, <c>Label⇥value</c> (HTML table cells), or a label line followed by its
    /// value line. Labels are compared without case and diacritics; the first occurrence wins.
    /// </summary>
    private static Dictionary<string, string> Labeled(string text)
    {
        var fields = new Dictionary<string, string>();
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        for (var i = 0; i < lines.Length; i++)
        {
            var match = LabelLine().Match(lines[i]);
            if (!match.Success)
            {
                continue;
            }

            var label = Fold(match.Groups["label"].Value);
            var value = match.Groups["value"].Value.Trim();
            if (value.Length == 0 && i + 1 < lines.Length && !LabelLine().Match(lines[i + 1]).Groups["value"].Success)
            {
                value = lines[i + 1];
            }

            if (value.Length > 0)
            {
                fields.TryAdd(label, value);
            }
        }

        return fields;
    }

    private static string Fold(string label)
    {
        var decomposed = label.Trim().TrimEnd(':').Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        return string.Concat(decomposed.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark));
    }

    [GeneratedRegex(@"na\s+účet\s+(?<account>CZ[\d ]{22,29}|[\d*xX•\- ]*\d[\d*xX•\- ]*/\s?\d{4})\s+(?:právě\s+)?dorazila\s+platba\s+ve\s+výši\s+(?<amount>\d[\d .]*(?:,\d+)?)\s*(?<currency>Kč|CZK|EUR|€)?",
        RegexOptions.IgnoreCase)]
    private static partial Regex IncomingSentence();

    [GeneratedRegex(@"z\s+účtu\s+\S+\s+(?:právě\s+)?odešla\s+platba", RegexOptions.IgnoreCase)]
    private static partial Regex OutgoingHint();

    [GeneratedRegex(@"^(?<label>[\p{L} ]{2,40}?)\s*(?::|\t)\s*(?<value>.*)$")]
    private static partial Regex LabelLine();

    [GeneratedRegex(@"\d{1,2}\.\s?\d{1,2}\.\s?\d{4}")]
    private static partial Regex DateText();
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
