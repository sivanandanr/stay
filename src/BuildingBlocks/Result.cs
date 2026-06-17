namespace Stay.BuildingBlocks;

/// <summary>Classifies an <see cref="Error"/> so the API can map it to an HTTP status.</summary>
public enum ErrorType
{
    Failure,      // 500 — unexpected
    Validation,   // 400
    Unauthorized, // 401
    Forbidden,    // 403
    NotFound,     // 404
    Conflict      // 409
}

/// <summary>
/// An expected failure. <see cref="Code"/> is a stable, machine-readable slug used as the
/// RFC 7807 <c>problem+json</c> <c>type</c> (e.g. <c>owner-not-approved</c>); <see cref="Details"/>
/// carries per-field messages for validation failures.
/// </summary>
public readonly record struct Error(
    string Code,
    string Message,
    ErrorType Type = ErrorType.Failure,
    IReadOnlyDictionary<string, string[]>? Details = null)
{
    public static Error Validation(string message, IReadOnlyDictionary<string, string[]>? details = null) =>
        new("validation", message, ErrorType.Validation, details);

    public static Error NotFound(string code, string message) => new(code, message, ErrorType.NotFound);

    public static Error Forbidden(string code, string message) => new(code, message, ErrorType.Forbidden);

    public static Error Conflict(string code, string message) => new(code, message, ErrorType.Conflict);
}

public sealed class Result<T>
{
    public bool IsSuccess { get; }
    public T? Value { get; }
    public Error? Error { get; }

    private Result(bool ok, T? value, Error? error) { IsSuccess = ok; Value = value; Error = error; }

    public static Result<T> Success(T value) => new(true, value, null);
    public static Result<T> Failure(Error error) => new(false, default, error);

    public static implicit operator Result<T>(Error error) => Failure(error);
}
