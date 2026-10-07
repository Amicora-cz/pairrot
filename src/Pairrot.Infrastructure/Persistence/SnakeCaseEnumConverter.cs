using System.Text.Json;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Pairrot.Infrastructure.Persistence;

/// <summary>Stores multi-word enums as snake_case strings ('unknown_recipient', 'retry_wait', ...).</summary>
internal sealed class SnakeCaseEnumConverter<TEnum>() : ValueConverter<TEnum, string>(
    value => SnakeCaseEnum<TEnum>.ToName(value),
    value => SnakeCaseEnum<TEnum>.Parse(value))
    where TEnum : struct, Enum;

internal static class SnakeCaseEnum<TEnum> where TEnum : struct, Enum
{
    private static readonly Dictionary<TEnum, string> Names =
        Enum.GetValues<TEnum>().ToDictionary(v => v, v => JsonNamingPolicy.SnakeCaseLower.ConvertName(v.ToString()));

    private static readonly Dictionary<string, TEnum> Values = Names.ToDictionary(p => p.Value, p => p.Key);

    public static string ToName(TEnum value) => Names[value];
    public static TEnum Parse(string value) => Values[value];

    /// <summary>The stored names, for check constraints: <c>'a', 'b'</c>.</summary>
    public static string SqlList => string.Join(", ", Names.Values.Select(n => $"'{n}'"));
}
