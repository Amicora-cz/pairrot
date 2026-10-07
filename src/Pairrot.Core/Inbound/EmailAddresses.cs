namespace Pairrot.Core.Inbound;

/// <summary>Normalizes provider-reported recipient addresses for comparison with the address map.</summary>
public static class EmailAddresses
{
    public const int MaxLength = 254;

    /// <summary>
    /// The lowercase bare address of <c>a@b</c> or <c>Name &lt;a@b&gt;</c>; <c>null</c> when the
    /// value is not one plausible address. Pairrot's own addresses are lowercase ASCII, so case
    /// folding never makes two of them equal by accident.
    /// </summary>
    public static string? Normalize(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var address = value.Trim();
        if (address.EndsWith('>') && address.LastIndexOf('<') is var open and >= 0)
        {
            address = address[(open + 1)..^1].Trim();
        }

        var at = address.IndexOf('@');
        if (address.Length is 0 or > MaxLength
            || at <= 0
            || at != address.LastIndexOf('@')
            || at == address.Length - 1
            || address.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '<' or '>' or ',' or ';'))
        {
            return null;
        }

        return address.ToLowerInvariant();
    }

    /// <summary>The distinct normalized addresses among <paramref name="values"/>, in their first order; invalid ones are skipped.</summary>
    public static List<string> NormalizeAll(IEnumerable<string?> values) =>
        values.Select(Normalize).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
}
