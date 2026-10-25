namespace Pairrot.Core.Payments;

/// <summary>A Czech calendar year's YYYYNNNNNN series, with sequence numbers 000001–999999.</summary>
public readonly record struct VariableSymbolSeries(long First, long Last)
{
    private static readonly TimeZoneInfo Prague = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");

    public static VariableSymbolSeries At(DateTimeOffset instant)
    {
        var prefix = TimeZoneInfo.ConvertTime(instant, Prague).Year * 1_000_000L;
        return new(prefix + 1, prefix + 999_999);
    }

    /// <summary>
    /// Resume this year's cursor (including its exhausted sentinel), or start at 000001 after
    /// a year change or migration from the legacy plain-number series. Historical reservations
    /// must still be checked under the account lock before a symbol is assigned.
    /// </summary>
    public long ResumeAt(long cursor) => cursor >= First && cursor <= Last + 1 ? cursor : First;
}
