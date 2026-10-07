using System.Text.Json.Serialization;
using Pairrot.Infrastructure.Tenants;

namespace Pairrot.Api.Http;

public sealed record ErrorBody(ErrorDetail Error);

/// <param name="Field">The request field a validation error belongs to; omitted when the error is not about one field.</param>
public sealed record ErrorDetail(
    string Code,
    string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Field = null);

/// <summary>All errors use <c>{"error": {"code": "...", "message": "...", "field": "..."}}</c>.</summary>
public static class ApiErrors
{
    public static IResult Result(int status, string code, string message, string? field = null) =>
        Results.Json(new ErrorBody(new ErrorDetail(code, message, field)), statusCode: status);

    public static IResult Validation(string message) =>
        Result(StatusCodes.Status400BadRequest, "invalid_request", message);

    public static IResult NotFound() =>
        Result(StatusCodes.Status404NotFound, "not_found", "The resource was not found.");

    public static IResult From(ServiceError error) => error.Kind switch
    {
        ServiceErrorKind.Validation => Result(StatusCodes.Status400BadRequest, error.Code, error.Message, error.Field),
        // Unknown and foreign resources look identical.
        ServiceErrorKind.NotFound => NotFound(),
        ServiceErrorKind.Conflict => Result(StatusCodes.Status409Conflict, error.Code, error.Message),
        ServiceErrorKind.Unauthorized => Result(StatusCodes.Status401Unauthorized, error.Code, error.Message),
        ServiceErrorKind.Forbidden => Result(StatusCodes.Status403Forbidden, error.Code, error.Message),
        ServiceErrorKind.PreconditionFailed => Result(StatusCodes.Status412PreconditionFailed, error.Code, error.Message),
        ServiceErrorKind.PreconditionRequired => Result(StatusCodes.Status428PreconditionRequired, error.Code, error.Message),
        _ => throw new ArgumentOutOfRangeException(nameof(error)),
    };

    public static Task WriteAsync(HttpContext context, int status, string code, string message) =>
        Result(status, code, message).ExecuteAsync(context);
}
