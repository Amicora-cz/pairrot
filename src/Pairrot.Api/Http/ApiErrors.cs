using Pairrot.Infrastructure.Tenants;

namespace Pairrot.Api.Http;

public sealed record ErrorBody(ErrorDetail Error);

public sealed record ErrorDetail(string Code, string Message);

/// <summary>All errors use <c>{"error": {"code": "...", "message": "..."}}</c>.</summary>
public static class ApiErrors
{
    public static IResult Result(int status, string code, string message) =>
        Results.Json(new ErrorBody(new ErrorDetail(code, message)), statusCode: status);

    public static IResult Validation(string message) =>
        Result(StatusCodes.Status400BadRequest, "invalid_request", message);

    public static IResult NotFound() =>
        Result(StatusCodes.Status404NotFound, "not_found", "The resource was not found.");

    public static IResult From(ServiceError error) => error.Kind switch
    {
        ServiceErrorKind.Validation => Result(StatusCodes.Status400BadRequest, error.Code, error.Message),
        // Unknown and foreign resources look identical.
        ServiceErrorKind.NotFound => NotFound(),
        ServiceErrorKind.Conflict => Result(StatusCodes.Status409Conflict, error.Code, error.Message),
        _ => throw new ArgumentOutOfRangeException(nameof(error)),
    };

    public static Task WriteAsync(HttpContext context, int status, string code, string message) =>
        Result(status, code, message).ExecuteAsync(context);
}
