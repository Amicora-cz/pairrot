using System.Text.Json;
using Pairrot.Infrastructure.Tenants;

namespace Pairrot.Api.Http;

/// <summary>
/// Reads a JSON object body field by field. Unknown or duplicate fields are rejected, and
/// absent fields are distinguishable from explicit nulls (needed for PATCH).
/// </summary>
public sealed class JsonBody
{
    private readonly Dictionary<string, JsonElement> _fields;

    private JsonBody(Dictionary<string, JsonElement> fields) => _fields = fields;

    public static async Task<(JsonBody? Body, IResult? Error)> ReadAsync(
        HttpRequest request, IReadOnlySet<string> allowedFields, bool allowEmpty = false)
    {
        if (allowEmpty && request.ContentLength is 0 or null && string.IsNullOrEmpty(request.ContentType))
        {
            return (new JsonBody([]), null);
        }

        if (!request.HasJsonContentType())
        {
            return (null, ApiErrors.Validation("The request body must be JSON (Content-Type: application/json)."));
        }

        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(request.Body, cancellationToken: request.HttpContext.RequestAborted);
        }
        catch (JsonException)
        {
            return (null, ApiErrors.Validation("The request body is not valid JSON."));
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, ApiErrors.Validation("The request body must be a JSON object."));
            }

            var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!allowedFields.Contains(property.Name))
                {
                    return (null, ApiErrors.Validation($"Unsupported field '{property.Name}'."));
                }

                if (!fields.TryAdd(property.Name, property.Value.Clone()))
                {
                    return (null, ApiErrors.Validation($"Duplicate field '{property.Name}'."));
                }
            }

            return (new JsonBody(fields), null);
        }
    }

    public bool Has(string name) => _fields.ContainsKey(name);

    /// <summary>
    /// Reads an optional whole-number field that may also be explicitly <c>null</c>. Fractions,
    /// exponents, strings, and values outside the 64-bit range are rejected, never rounded.
    /// </summary>
    public bool TryGetInteger(string name, out Optional<long?> value, out IResult? error)
    {
        value = Optional<long?>.Unset;
        error = null;

        if (!_fields.TryGetValue(name, out var element))
        {
            return true;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
                value = new Optional<long?>(true, null);
                return true;
            case JsonValueKind.Number when element.TryGetInt64(out var number):
                value = new Optional<long?>(true, number);
                return true;
            default:
                error = ApiErrors.Result(StatusCodes.Status400BadRequest, "invalid_request",
                    $"Field '{name}' must be a whole number.", name);
                return false;
        }
    }

    /// <summary>Reads an optional positive version number; absent or <c>null</c> gives <c>null</c>.</summary>
    public bool TryGetVersion(string name, out int? value, out IResult? error)
    {
        value = null;
        if (!TryGetInteger(name, out var number, out error))
        {
            return false;
        }

        if (number.Value is not { } version)
        {
            return true;
        }

        if (version is < 1 or > int.MaxValue)
        {
            error = ApiErrors.Result(StatusCodes.Status400BadRequest, "invalid_request", $"Field '{name}' must be a positive version number.", name);
            return false;
        }

        value = (int)version;
        return true;
    }

    /// <summary>Reads an optional object of <c>{"id": version}</c>; absent or <c>null</c> gives <c>null</c>.</summary>
    public bool TryGetVersionMap(string name, out Dictionary<Guid, int>? value, out IResult? error)
    {
        value = null;
        error = null;
        if (!_fields.TryGetValue(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        var map = new Dictionary<Guid, int>();
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (!Guid.TryParse(property.Name, out var id) || property.Value.ValueKind != JsonValueKind.Number
                    || !property.Value.TryGetInt32(out var version) || version < 1 || !map.TryAdd(id, version))
                {
                    map = null;
                    break;
                }
            }
        }
        else
        {
            map = null;
        }

        if (map is null)
        {
            error = ApiErrors.Result(StatusCodes.Status400BadRequest, "invalid_request",
                $"Field '{name}' must be an object mapping transaction IDs to their versions.", name);
            return false;
        }

        value = map;
        return true;
    }

    /// <summary>Reads an optional string field that may also be explicitly <c>null</c>.</summary>
    public bool TryGetString(string name, out Optional<string?> value, out IResult? error)
    {
        value = Optional<string?>.Unset;
        error = null;

        if (!_fields.TryGetValue(name, out var element))
        {
            return true;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
                value = new Optional<string?>(true, null);
                return true;
            case JsonValueKind.String:
                value = new Optional<string?>(true, element.GetString());
                return true;
            default:
                error = ApiErrors.Result(StatusCodes.Status400BadRequest, "invalid_request", $"Field '{name}' must be a string.", name);
                return false;
        }
    }
}
