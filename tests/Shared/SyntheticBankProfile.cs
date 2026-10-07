using System.Text;
using Pairrot.Core.Banking;
using Pairrot.Core.Parsing;

namespace Pairrot.Tests.Shared;

/// <summary>
/// A test-only bank profile with an invented, obviously synthetic notification format. It exists
/// to exercise the common pipeline (trust, normalization, duplicates, reprocessing) while no real
/// Česká spořitelna sample is available. It is never registered outside tests and says nothing
/// about any real bank's format.
/// <code>
/// PAIRROT SYNTHETIC NOTICE
/// Type: incoming | outgoing | balance | pending | correction | multi
/// Account: 19-2000145399/0800        (or masked: ******5399/0800)
/// Amount: 1 234,50 CZK
/// Booked: 07.10.2026
/// Time: 07.10.2026 09:15
/// VS: 123
/// Reference: R-1
/// From account: 123-456/0100
/// From name: Jana Nováková
/// Message: Lekce
/// </code>
/// </summary>
public sealed class SyntheticBankProfile : IBankProfile
{
    public const string ProfileName = "synthetic_test";
    public const string Header = "PAIRROT SYNTHETIC NOTICE";
    public const string Sender = "notify@bank.pairrot-fixture.net";

    public string Name => ProfileName;

    /// <summary>Changed by tests to simulate a parser upgrade.</summary>
    public string Version { get; set; } = "1.0.0";

    public string BankCode { get; init; } = "0800";

    public SenderPolicy Senders { get; } = new(new HashSet<string>(), new HashSet<string> { "bank.pairrot-fixture.net" });

    public IReadOnlyList<SourcePart> Parts { get; init; } = [SourcePart.Text, SourcePart.Html];

    public BankProfileFormats Formats { get; } = new(
        ',', ".", ["dd.MM.yyyy"], ["dd.MM.yyyy HH:mm"], "Europe/Prague", new Dictionary<string, string> { ["KČ"] = "CZK" });

    public bool HasStableReference { get; init; } = true;

    /// <summary>A simulated parser change applied to every extraction.</summary>
    public Func<ExtractedFields, ExtractedFields>? Adjust { get; set; }

    public TemplateMatch Recognize(string content, SourcePart part, string? subject)
    {
        var lines = content.Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != Header)
        {
            return TemplateMatch.Unrecognized;
        }

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0)
            {
                fields[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
        }

        var kind = fields.GetValueOrDefault("Type") switch
        {
            "incoming" => MessageKind.IncomingTransfer,
            "outgoing" => MessageKind.OutgoingTransfer,
            "balance" => MessageKind.BalanceSummary,
            "pending" => MessageKind.PendingMovement,
            "correction" => MessageKind.Correction,
            "multi" => MessageKind.MultipleMovements,
            _ => MessageKind.Unrecognized,
        };
        if (kind != MessageKind.IncomingTransfer)
        {
            return new TemplateMatch(kind, kind == MessageKind.Unrecognized ? null : "synthetic_v1");
        }

        string? amount = null, currency = null;
        if (fields.GetValueOrDefault("Amount") is { } amountText)
        {
            var space = amountText.LastIndexOf(' ');
            (amount, currency) = space > 0 && amountText[(space + 1)..].All(char.IsLetter)
                ? (amountText[..space], amountText[(space + 1)..])
                : (amountText, null);
        }

        var extracted = new ExtractedFields
        {
            Amount = amount,
            Currency = currency,
            VariableSymbol = fields.GetValueOrDefault("VS"),
            DestinationAccount = fields.GetValueOrDefault("Account"),
            SenderAccount = fields.GetValueOrDefault("From account"),
            SenderName = fields.GetValueOrDefault("From name"),
            Message = fields.GetValueOrDefault("Message"),
            BookedOn = fields.GetValueOrDefault("Booked"),
            OccurredAt = fields.GetValueOrDefault("Time"),
            BankReference = fields.GetValueOrDefault("Reference"),
        };
        return new TemplateMatch(kind, "synthetic_v1", Adjust is { } adjust ? adjust(extracted) : extracted);
    }

    /// <summary>Synthetic rule: six asterisks, the last four digits of the number, and the bank code.</summary>
    public bool MaskedDestinationMatches(string masked, CzechBankAccount destination) =>
        masked == $"******{destination.Number[^4..]}/{destination.BankCode}";

    /// <summary>Builds a notice; <paramref name="fields"/> are written in order, <c>null</c> values omitted.</summary>
    public static string Notice(params (string Key, string? Value)[] fields)
    {
        var text = new StringBuilder(Header).Append('\n');
        foreach (var (key, value) in fields)
        {
            if (value is not null)
            {
                text.Append(key).Append(": ").Append(value).Append('\n');
            }
        }

        return text.ToString();
    }

    /// <summary>The same notice as an HTML table, like a bank's HTML part.</summary>
    public static string NoticeHtml(params (string Key, string? Value)[] fields)
    {
        var html = new StringBuilder($"<html><head><style>td{{color:red}}</style><script>alert(1)</script></head><body><h1>{Header}</h1><table>");
        foreach (var (key, value) in fields)
        {
            if (value is not null)
            {
                html.Append($"<tr><td>{System.Net.WebUtility.HtmlEncode(key)}:</td><td>{System.Net.WebUtility.HtmlEncode(value)}</td></tr>");
            }
        }

        return html.Append("</table><img src=\"https://tracker.example/x.png\"></body></html>").ToString();
    }
}
