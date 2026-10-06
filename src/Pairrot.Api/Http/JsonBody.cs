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
