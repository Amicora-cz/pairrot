using System.Globalization;
using System.Net;
using System.Text;
using Pairrot.Core.Banking;

namespace Pairrot.Core.Parsing;

/// <summary>
/// Feature 021: the profile of a simulated bank, used only in sandbox mode as the fallback for bank
/// codes without a real profile. Its notification is an invented plain-text template with labeled
/// lines; it says nothing about any real bank's format.
/// <code>
/// PAIRROT SANDBOX NOTICE
/// Direction: incoming | outgoing
/// Account: 19-2000145399/0100
/// Amount: 450,00 CZK
/// Booking date: 25.10.2026
/// Booked at: 25.10.2026 09:15
/// VS: 2026000001
/// Message: Lekce 12. 11.
/// Counterparty account: 19-2000145399/0100
/// Counterparty name: Jana Nováková
/// Reference: SBX-0001
/// </code>
/// </summary>
public sealed class SandboxBankProfile : IBankProfile
{
    public const string ProfileName = "pairrot_sandbox";
    public const string Header = "PAIRROT SANDBOX NOTICE";
    public const string Sender = "notifikace@banka.sandbox.pairrot.test";

    public string Name => ProfileName;
    public string Version => "1.0.0";

    /// <summary>Not a real bank code: the registry returns this profile only for codes that have none.</summary>
    public string BankCode => "*";

    public SenderPolicy Senders { get; } = new(new HashSet<string> { Sender }, new HashSet<string>());

    public IReadOnlyList<SourcePart> Parts => [SourcePart.Text];

    public BankProfileFormats Formats { get; } = new(
        ',', "", ["dd.MM.yyyy"], ["dd.MM.yyyy HH:mm"], "Europe/Prague", new Dictionary<string, string> { ["KČ"] = "CZK" });

    public bool HasStableReference => true;

    public TemplateMatch Recognize(string content, SourcePart part, string? subject)
    {
        var lines = content.Split('\n').Select(l => l.Trim()).ToArray();
        if (lines.Length == 0 || lines[0] != Header)
        {
            return TemplateMatch.Unrecognized;
        }

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0)
            {
                fields.TryAdd(line[..colon].Trim(), line[(colon + 1)..].Trim());
            }
        }

        var kind = fields.GetValueOrDefault("Direction") switch
        {
            "incoming" => MessageKind.IncomingTransfer,
            "outgoing" => MessageKind.OutgoingTransfer,
            _ => MessageKind.Unrecognized,
        };
        if (kind != MessageKind.IncomingTransfer)
        {
            return new TemplateMatch(kind, kind == MessageKind.Unrecognized ? null : "sandbox_outgoing_v1");
        }

        string? amount = null, currency = null;
        if (fields.GetValueOrDefault("Amount") is { } amountText)
        {
            var space = amountText.LastIndexOf(' ');
            (amount, currency) = space > 0 && amountText[(space + 1)..].All(char.IsLetter)
                ? (amountText[..space], amountText[(space + 1)..])
                : (amountText, null);
        }

        return new TemplateMatch(kind, "sandbox_incoming_v1", new ExtractedFields
        {
            Amount = amount,
            Currency = currency,
            VariableSymbol = fields.GetValueOrDefault("VS"),
            DestinationAccount = fields.GetValueOrDefault("Account"),
            SenderAccount = fields.GetValueOrDefault("Counterparty account"),
            SenderName = fields.GetValueOrDefault("Counterparty name"),
            Message = fields.GetValueOrDefault("Message"),
            BookedOn = fields.GetValueOrDefault("Booking date"),
            OccurredAt = fields.GetValueOrDefault("Booked at"),
            BankReference = fields.GetValueOrDefault("Reference"),
        });
    }

    /// <summary>The sandbox notice always prints the full account.</summary>
    public bool MaskedDestinationMatches(string masked, CzechBankAccount destination) => false;
}

/// <summary>One simulated movement on the receiving account, before it is rendered as a notification.</summary>
public sealed record SandboxPayment(
    CzechBankAccount Destination,
    string Currency,
    long Amount,
    DateTimeOffset BookedAt,
    CzechBankAccount PayerAccount,
    string PayerName,
    string? VariableSymbol = null,
    string? Message = null,
    string? BankReference = null,
    bool Incoming = true);

/// <summary>A rendered notification: the content of an email as the bank would have sent it.</summary>
public sealed record SandboxNotice(string HeaderFrom, string EnvelopeSender, string Subject, string? Text, string? Html);

/// <summary>Renders a simulated payment in the notification template of one bank profile; the profile parses it back.</summary>
public interface ISandboxNoticeRenderer
{
    /// <summary>The <see cref="IBankProfile.Name"/> this renderer writes for.</summary>
    string ProfileName { get; }

    /// <summary>Whether the template can carry a bank reference.</summary>
    bool SupportsBankReference { get; }

    SandboxNotice Render(SandboxPayment payment);
}

/// <summary>Shared formatting of simulated notices.</summary>
internal static class SandboxFormat
{
    public static DateTime Prague(DateTimeOffset value) =>
        TimeZoneInfo.ConvertTime(value, TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague")).DateTime;

    /// <summary>Minor units as <c>1234,50</c>, with <paramref name="group"/> between thousands when given.</summary>
    public static string Amount(long minor, string? group = null)
    {
        var whole = (minor / 100).ToString(CultureInfo.InvariantCulture);
        if (group is not null)
        {
            for (var i = whole.Length - 3; i > 0; i -= 3)
            {
                whole = whole.Insert(i, group);
            }
        }

        return $"{whole},{minor % 100:00}";
    }

    /// <summary>One line of text: line breaks would let a value forge another labeled line.</summary>
    public static string Line(string? value) =>
        string.Join(' ', (value ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
}

/// <summary>Renders the sandbox profile's labeled-line template.</summary>
public sealed class SandboxProfileRenderer : ISandboxNoticeRenderer
{
    public string ProfileName => SandboxBankProfile.ProfileName;
    public bool SupportsBankReference => true;

    public SandboxNotice Render(SandboxPayment payment)
    {
        var booked = SandboxFormat.Prague(payment.BookedAt);
        var text = new StringBuilder(SandboxBankProfile.Header).Append('\n');
        void Add(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                text.Append(label).Append(": ").Append(SandboxFormat.Line(value)).Append('\n');
            }
        }

        Add("Direction", payment.Incoming ? "incoming" : "outgoing");
        Add("Account", payment.Destination.AccountNumber);
        Add("Amount", $"{SandboxFormat.Amount(payment.Amount)} {payment.Currency}");
        Add("Booking date", booked.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture));
        Add("Booked at", booked.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture));
        Add("VS", payment.VariableSymbol);
        Add("Message", payment.Message);
        Add("Counterparty account", payment.PayerAccount.AccountNumber);
        Add("Counterparty name", payment.PayerName);
        Add("Reference", payment.BankReference);
        return new SandboxNotice(
            SandboxBankProfile.Sender, SandboxBankProfile.Sender,
            payment.Incoming ? "Sandbox: incoming payment" : "Sandbox: outgoing payment", text.ToString(), null);
    }
}

/// <summary>Renders the George "Příchozí platby" notification of <see cref="CeskaSporitelnaProfile"/> (HTML only, like the bank's).</summary>
public sealed class CeskaSporitelnaRenderer : ISandboxNoticeRenderer
{
    public const string Sender = "ceskasporitelna@csas.cz";

    public string ProfileName => CeskaSporitelnaProfile.ProfileName;
    public bool SupportsBankReference => false;

    public SandboxNotice Render(SandboxPayment payment)
    {
        const string nbsp = "&nbsp;";
        var booked = SandboxFormat.Prague(payment.BookedAt);
        var currency = payment.Currency == CzechBankAccount.Currency ? "Kč" : payment.Currency;
        var amount = $"{SandboxFormat.Amount(payment.Amount, nbsp)}{nbsp}{currency}";
        var paidAt = booked.ToString("d.'&nbsp;'M.'&nbsp;'yyyy H:mm", CultureInfo.InvariantCulture);
        var account = WebUtility.HtmlEncode(payment.Destination.AccountNumber);

        var html = new StringBuilder("<!doctype html><html><head><title></title></head><body><div>");
        void Plain(string text) => html.Append("<div><span>").Append(text).Append("</span></div>");
        void Labeled(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                html.Append("<div><span>").Append(label).Append(": </span><span><b>")
                    .Append(WebUtility.HtmlEncode(SandboxFormat.Line(value))).Append("</b></span></div>");
            }
        }

        Plain("Dobrý den,");
        Plain(payment.Incoming
            ? $"na účet <b>{account}</b> právě dorazila platba ve výši <b>{amount}.</b>"
            : $"z účtu <b>{account}</b> právě odešla platba ve výši <b>{amount}.</b>");
        Plain("Vaše Česká spořitelna");
        Plain("<b>Informace o transakci</b>");
        Labeled("Datum a čas platby", paidAt.Replace(nbsp, " "));
        Labeled("Směr platby", payment.Incoming ? "příchozí" : "odchozí");
        Labeled("Číslo účtu", payment.Destination.AccountNumber);
        Labeled("Číslo účtu protistrany", payment.PayerAccount.AccountNumber);
        Labeled("Název účtu protistrany", payment.PayerName);
        Labeled("Částka v měně transakce", amount.Replace(nbsp, " "));
        Labeled("Částka v měně účtu", amount.Replace(nbsp, " "));
        Labeled("Variabilní symbol", payment.VariableSymbol);
        Labeled("Zpráva pro příjemce", payment.Message);
        html.Append("</div></body></html>");
        return new SandboxNotice(Sender, Sender, payment.Incoming ? "Příchozí platba" : "Odešla platba", null, html.ToString());
    }
}
