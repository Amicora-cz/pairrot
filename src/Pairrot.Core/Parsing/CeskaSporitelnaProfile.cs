using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Pairrot.Core.Banking;

namespace Pairrot.Core.Parsing;

/// <summary>
/// Česká spořitelna (bank code 0800): the George "Informační zprávy → Příchozí platby" e-mail.
/// <para>
/// Verified against one real notification (2026-10-07, received through an automatic Gmail
/// forward with the bank's DKIM intact, DMARC <c>pass</c>), kept sanitized as
/// <c>tests/Fixtures/ceska-sporitelna/incoming-transfer-with-vs.html</c>. The bank sends HTML only.
/// Its content, reduced to text:
/// </para>
/// <code>
/// Dobrý den, pane …,
/// na účet &lt;holder name&gt; 19-2000145399/0800 právě dorazila platba ve výši 20,00 Kč.
/// Na účtu je nově k dispozici 1 000,00 Kč.
/// Informace o transakci
/// Datum a čas platby: 7. 10. 2026 14:29
/// Směr platby: příchozí
/// Číslo účtu: 19-2000145399/0800
/// Číslo účtu protistrany: 2000145399/0300
/// Částka v měně transakce: 20,00 Kč
/// Částka v měně účtu: 20,00 Kč
/// Variabilní symbol: …
/// Zpráva pro příjemce: …
/// </code>
/// <list type="bullet">
/// <item>Direction comes from "Směr platby" (falling back to the opening sentence); only
/// <c>příchozí</c> is an incoming payment.</item>
/// <item>The amount is "Částka v měně účtu", cross-checked with the opening sentence; the
/// balance line is never read. A disagreement is not guessed: the message is unsupported.</item>
/// <item>"Datum a čas platby" gives the booking date and the time (Europe/Prague); the
/// notification has no separate booking date.</item>
/// <item>The notification carries no transaction reference, so lookalike payments are flagged, not merged.</item>
/// <item>Not yet seen in a real sample: a payment without VS, outgoing payments, corrections,
/// a counterparty name, masked accounts, and a text part. Labels for those are best guesses that
/// only add optional fields; missing required fields make the message <c>review_required</c>.</item>
/// </list>
/// </summary>
public sealed partial class CeskaSporitelnaProfile : IBankProfile
{
    public const string ProfileName = "ceska_sporitelna";

    public string Name => ProfileName;
    public string Version => "1.0.0";
    public string BankCode => "0800";

    /// <summary>The bank's own domain (DMARC <c>p=reject</c>); DMARC <c>pass</c> ties the From address to it.</summary>
    public SenderPolicy Senders { get; } = new(new HashSet<string> { "ceskasporitelna@csas.cz" }, new HashSet<string> { "csas.cz" });

    /// <summary>The verified sample has HTML only; a text part, if any, must agree with it.</summary>
    public IReadOnlyList<SourcePart> Parts => [SourcePart.Html, SourcePart.Text];

    public BankProfileFormats Formats { get; } = new(
        ',', ".", ["d.M.yyyy"], ["d.M.yyyy H:mm"], "Europe/Prague", new Dictionary<string, string> { ["KČ"] = "CZK", ["CZK"] = "CZK" });

    public bool HasStableReference => false;

    public TemplateMatch Recognize(string content, SourcePart part, string? subject)
    {
        var text = content.Replace(' ', ' ');
        var labeled = Labeled(text);
        string? Field(params string[] labels) => labels.Select(l => labeled.GetValueOrDefault(Fold(l))).FirstOrDefault(v => v is not null);

        var incoming = IncomingSentence().Match(text);
        var outgoing = OutgoingSentence().IsMatch(text) || subject?.Contains("Odešla platba", StringComparison.OrdinalIgnoreCase) == true;
        var kind = Field("Směr platby") is { } direction
            ? Fold(direction) switch
            {
                "prichozi" => MessageKind.IncomingTransfer,
                "odchozi" => MessageKind.OutgoingTransfer,
                _ => MessageKind.Unrecognized,
            }
            : incoming.Success ? MessageKind.IncomingTransfer
            : outgoing ? MessageKind.OutgoingTransfer
            : MessageKind.Unrecognized;

        if (kind == MessageKind.OutgoingTransfer)
        {
            return new TemplateMatch(MessageKind.OutgoingTransfer, "george_outgoing");
        }

        // An unknown direction, or an incoming direction with an outgoing sentence, is never recorded as a payment.
        if (kind == MessageKind.Unrecognized || outgoing)
        {
            return TemplateMatch.Unrecognized;
        }

        var (amount, currency) = Split(Field("Částka v měně účtu"));
        var (sentenceAmount, sentenceCurrency) = incoming.Success
            ? (incoming.Groups["amount"].Value.Trim(), incoming.Groups["currency"].Value)
            : ((string?)null, (string?)null);
        if (amount is not null && sentenceAmount is not null && Compact(amount) != Compact(sentenceAmount))
        {
            return new TemplateMatch(MessageKind.Unrecognized, "george_incoming_amount_conflict");
        }

        var paidAt = Field("Datum a čas platby", "Datum a čas transakce") is { } when ? Dates(when) : null;
        var booked = paidAt is not null && DateText().Match(paidAt) is { Success: true } date
            ? date.Value
            : Field("Datum zaúčtování") is { } bookedText ? Dates(bookedText) : null;

        return new TemplateMatch(MessageKind.IncomingTransfer, "george_incoming_v1", new ExtractedFields
        {
            Amount = amount ?? sentenceAmount,
            Currency = amount is not null ? currency : sentenceCurrency,
            DestinationAccount = Field("Číslo účtu") ?? (incoming.Success ? incoming.Groups["account"].Value.Trim() : null),
            VariableSymbol = Field("Variabilní symbol"),
            SenderAccount = Field("Číslo účtu protistrany", "Účet protistrany", "Protiúčet"),
            SenderName = Field("Název účtu protistrany", "Název protistrany", "Jméno protistrany", "Název protiúčtu"),
            Message = Field("Zpráva pro příjemce"),
            BookedOn = booked,
            OccurredAt = paidAt,
        });
    }

    /// <summary>The bank's masking is unknown, so no masked account is sufficient.</summary>
    public bool MaskedDestinationMatches(string masked, CzechBankAccount destination) => false;

    /// <summary><c>20,00 Kč</c> → (<c>20,00</c>, <c>Kč</c>).</summary>
    private static (string? Amount, string? Currency) Split(string? value)
    {
        if (value is null)
        {
            return (null, null);
        }

        var match = AmountWithCurrency().Match(value.Trim());
        return match.Success ? (match.Groups["amount"].Value.Trim(), match.Groups["currency"].Value) : (value.Trim(), null);
    }

    private static string Compact(string amount) => Regex.Replace(amount, @"[\s.]", "");

    /// <summary><c>7. 10. 2026 14:29</c> → <c>7.10.2026 14:29</c>, the profile's exact format.</summary>
    private static string Dates(string value) => Regex.Replace(value, @"\.\s+", ".").Trim();

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
            if (value.Length == 0 && i + 1 < lines.Length && !LabelLine().Match(lines[i + 1]).Success)
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

    // "na účet <holder name> 19-2000145399/0800 právě dorazila platba ve výši 20,00 Kč."
    [GeneratedRegex(@"na\s+účet\s+(?:.*?\s)?(?<account>CZ\d{2}(?:\s?\d{4}){5}|[\d*xX•-]*\d[\d*xX•-]*/\d{4})\s+(?:právě\s+)?dorazila\s+platba\s+ve\s+výši\s+(?<amount>\d[\d .]*(?:,\d+)?)\s*(?<currency>Kč|CZK|EUR|€)?",
        RegexOptions.IgnoreCase)]
    private static partial Regex IncomingSentence();

    [GeneratedRegex(@"z\s+účtu\s+.*?\bodešla\s+platba", RegexOptions.IgnoreCase)]
    private static partial Regex OutgoingSentence();

    [GeneratedRegex(@"^(?<amount>\d[\d .]*(?:,\d+)?)\s*(?<currency>\p{L}+|€)?$")]
    private static partial Regex AmountWithCurrency();

    [GeneratedRegex(@"^(?<label>[\p{L} ]{2,40}?)\s*(?::|\t)\s*(?<value>.*)$")]
    private static partial Regex LabelLine();

    [GeneratedRegex(@"\d{1,2}\.\d{1,2}\.\d{4}")]
    private static partial Regex DateText();
}

/// <summary>The installed bank profiles, one per bank code. Tenants cannot add or choose profiles.</summary>
public sealed class BankProfileRegistry
{
    private readonly Dictionary<string, IBankProfile> _byBankCode;
    private readonly IBankProfile? _fallback;

    /// <param name="fallback">Feature 021: handles bank codes without a profile; given only in sandbox mode.</param>
    public BankProfileRegistry(IEnumerable<IBankProfile> profiles, IBankProfile? fallback = null)
    {
        _fallback = fallback;
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

    /// <summary>The bank-code profiles, then the fallback if there is one.</summary>
    public IReadOnlyList<IBankProfile> Installed => _fallback is null ? [.. _byBankCode.Values] : [.. _byBankCode.Values, _fallback];

    public IBankProfile? For(CzechBankAccount account) => _byBankCode.GetValueOrDefault(account.BankCode) ?? _fallback;
}
