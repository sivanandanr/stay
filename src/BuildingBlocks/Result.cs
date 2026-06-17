namespace Stay.BuildingBlocks;

public readonly record struct Error(string Code, string Message);

public sealed class Result<T>
{
    public bool IsSuccess { get; }
    public T? Value { get; }
    public Error? Error { get; }
    private Result(bool ok, T? value, Error? error) { IsSuccess = ok; Value = value; Error = error; }
    public static Result<T> Success(T value) => new(true, value, null);
    public static Result<T> Failure(Error error) => new(false, default, error);
}
