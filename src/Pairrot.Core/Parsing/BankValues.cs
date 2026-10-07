using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Pairrot.Core.Parsing;

public enum AmountError
{
    Missing,
    Invalid,

    /// <summary>More decimal places than the currency has; never rounded.</summary>
    Precision,

    /// <summary>Does not fit a signed 64-bit count of minor units.</summary>
    Overflow,

    /// <summary>Zero or signed; direction comes from the template, not from a sign.</summary>
    NotPositive,
}

/// <summary>Exact parsing of printed money amounts into minor units; no floating point, no rounding.</summary>
public static class BankAmount
{
    /// <summary>Minor-unit digits of supported currencies.</summary>
    public static readonly IReadOnlyDictionary<string, int> Precision = new Dictionary<string, int> { ["CZK"] = 2, ["EUR"] = 2 };

    /// <summary>ISO codes recognized in notifications; only the connection's currency is accepted.</summary>
    public static readonly IReadOnlySet<string> KnownCurrencies = new HashSet<string> { "CZK", "EUR", "USD", "GBP", "CHF", "PLN", "HUF" };

    /// <summary>
    /// Parses e.g. <c>1 234,50</c> with decimal separator <c>,</c>. Whitespace (including no-break
    /// spaces) and <paramref name="groupSeparators"/> group thousands, but only in groups of three.
    /// </summary>
    public static AmountError? TryParseMinorUnits(
        string? raw, char decimalSeparator, string groupSeparators, int precision, out long minorUnits)
    {
        minorUnits = 0;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return AmountError.Missing;
        }

        var value = raw.Trim();
        if (value[0] is '-' or '+' or '−')
        {
            return AmountError.NotPositive;
        }

        var separator = value.IndexOf(decimalSeparator);
        if (separator != value.LastIndexOf(decimalSeparator))
        {
            return AmountError.Invalid;
        }

        var integerPart = separator < 0 ? value : value[..separator];
        var fraction = separator < 0 ? "" : value[(separator + 1)..];
        if (!fraction.All(char.IsAsciiDigit) || separator >= 0 && fraction.Length == 0)
        {
            return AmountError.Invalid;
        }

        var groups = Regex.Split(integerPart, $"[\\s  {Regex.Escape(groupSeparators)}]");
        if (groups.Any(g => g.Length == 0 || !g.All(char.IsAsciiDigit))
            || groups.Length > 1 && (groups[0].Length > 3 || groups.Skip(1).Any(g => g.Length != 3)))
        {
            return AmountError.Invalid;
        }

        if (fraction.Length > precision)
        {
            return AmountError.Precision;
        }

        try
        {
            var units = 0L;
            foreach (var digit in string.Concat(groups) + fraction.PadRight(precision, '0'))
            {
                units = checked(units * 10 + (digit - '0'));
            }

            minorUnits = units;
        }
        catch (OverflowException)
        {
            return AmountError.Overflow;
        }

        return minorUnits > 0 ? null : AmountError.NotPositive;
    }
}

/// <summary>
/// Reduces email HTML to inert text for template matching: no script runs, no resource loads, no
/// link is followed. Structure becomes line breaks; table cells become tabs.
/// </summary>
public static partial class HtmlText
{
    public static string ToText(string html)
    {
        var text = Dropped().Replace(html, " ");
        text = LineBreak().Replace(text, "\n");
        text = Cell().Replace(text, "\t");
        text = Tag().Replace(text, "");
        return Lines(WebUtility.HtmlDecode(text));
    }

    /// <summary>Normalized lines: unified line endings, no-break spaces as spaces, collapsed spaces, no blank lines.</summary>
    public static string Lines(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
            .Select(line => Spaces().Replace(line.Replace(' ', ' ').Replace(' ', ' '), " ").Trim(' ', '\t'))
            .Where(line => line.Length > 0);
        return string.Join('\n', lines);
    }

    [GeneratedRegex(@"<(script|style|head|title|template)\b[^>]*>.*?</\1\s*>|<!--.*?-->", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Dropped();

    [GeneratedRegex(@"<br\s*/?>|</(p|div|tr|li|h[1-6]|table|ul|ol|section|article|header|footer)\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreak();

    [GeneratedRegex(@"</t[dh]\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex Cell();

    [GeneratedRegex(@"<[^>]*>", RegexOptions.Singleline)]
    private static partial Regex Tag();

    [GeneratedRegex(@"[ ]{2,}")]
    private static partial Regex Spaces();
}

/// <summary>Dates and timestamps printed by a bank, parsed only in the profile's exact formats.</summary>
public static class BankDates
{
    public static bool TryParseDate(string? raw, string[] formats, out DateOnly date)
    {
        date = default;
        return !string.IsNullOrWhiteSpace(raw) && formats.Length > 0
            && DateOnly.TryParseExact(raw.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    /// <summary>
    /// A timestamp with its own offset, or a local time in <paramref name="timeZoneId"/>. A local
    /// time that occurs twice (autumn clock change) or never is refused rather than guessed.
    /// </summary>
    public static TimestampResult TryParseTimestamp(string? raw, string[] formats, string timeZoneId, out DateTimeOffset value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return TimestampResult.Missing;
        }

        var text = raw.Trim();
        var withOffset = formats.Where(f => f.Contains('z') || f.Contains('K')).ToArray();
        if (withOffset.Length > 0
            && DateTimeOffset.TryParseExact(text, withOffset, CultureInfo.InvariantCulture, DateTimeStyles.None, out value))
        {
            return TimestampResult.Parsed;
        }

        var local = formats.Except(withOffset).ToArray();
        if (local.Length == 0
            || !DateTime.TryParseExact(text, local, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            return TimestampResult.Invalid;
        }

        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        if (zone.IsAmbiguousTime(time) || zone.IsInvalidTime(time))
        {
            return TimestampResult.Ambiguous;
        }

        value = new DateTimeOffset(time, zone.GetUtcOffset(time));
        return TimestampResult.Parsed;
    }

    public enum TimestampResult
    {
        Parsed,
        Missing,
        Invalid,
        Ambiguous,
    }
}

internal static class TextLimits
{
    public static string? Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim().Replace('\0', '�');
        return trimmed.Length <= maxLength ? trimmed : new StringBuilder(trimmed, 0, maxLength, maxLength).ToString();
    }
}
