namespace Pairrot.Infrastructure.Tenants;

public enum ServiceErrorKind
{
    Validation,
    NotFound,
    Conflict,
    Unauthorized,
    Forbidden,
}

/// <param name="Code">Stable machine-readable error code.</param>
/// <param name="Field">The request field a validation error belongs to, if any.</param>
public sealed record ServiceError(ServiceErrorKind Kind, string Code, string Message, string? Field = null)
{
    public static ServiceError Validation(string message) => new(ServiceErrorKind.Validation, "invalid_request", message);

    public static ServiceError Validation(string code, string field, string message) =>
        new(ServiceErrorKind.Validation, code, message, field);

    public static ServiceError NotFound(string message = "The resource was not found.") =>
        new(ServiceErrorKind.NotFound, "not_found", message);

    public static ServiceError Conflict(string code, string message) => new(ServiceErrorKind.Conflict, code, message);

    public static ServiceError Forbidden(string message) => new(ServiceErrorKind.Forbidden, "forbidden", message);
}

public readonly record struct ServiceResult<T>
{
    private ServiceResult(T? value, ServiceError? error)
    {
        Value = value;
        Error = error;
    }

    public T? Value { get; }
    public ServiceError? Error { get; }

    public static implicit operator ServiceResult<T>(T value) => new(value, null);
    public static implicit operator ServiceResult<T>(ServiceError error) => new(default, error);
}

/// <summary>A field that may be absent from a PATCH request, as opposed to explicitly set to null.</summary>
public readonly record struct Optional<T>(bool HasValue, T Value)
{
    public static Optional<T> Unset => default;
    public static implicit operator Optional<T>(T value) => new(true, value);
}

/// <summary>Who performed a change: exactly one of a signed-in user, an API key, or a named administrative operation.</summary>
public sealed record Actor(Guid? UserId, Guid? ApiKeyId, string? Description)
{
    public static Actor User(Guid userId) => new(userId, null, null);
    public static Actor Key(Guid apiKeyId) => new(null, apiKeyId, null);
    public static Actor Administrator(string description) => new(null, null, description);
}

/// <summary>A newly created key. <see cref="Secret"/> is returned once and never stored.</summary>
public sealed record IssuedApiKey(Core.Tenants.TenantApiKey Key, string Secret);
