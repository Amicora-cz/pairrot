using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Pairrot.Infrastructure.Persistence;

/// <summary>Stores enums as lowercase strings ('active', 'management', ...) as the schema expects.</summary>
internal sealed class LowercaseEnumConverter<TEnum>() : ValueConverter<TEnum, string>(
    value => value.ToString().ToLowerInvariant(),
    value => Enum.Parse<TEnum>(value, true))
    where TEnum : struct, Enum;
