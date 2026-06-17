using Microsoft.AspNetCore.Http;

namespace Stay.BuildingBlocks.Http;

/// <summary>
/// Maps a <see cref="Result{T}"/> to an HTTP response, rendering failures as RFC 7807
/// <c>problem+json</c> with a stable, machine-readable <c>type</c> slug (CLAUDE.md §5).
/// </summary>
public static class ResultHttpExtensions
{
    private const string TypeBase = "https://stay.platform/errors/";

    public static IResult ToHttp<T>(this Result<T> result, Func<T, IResult> onSuccess) =>
        result.IsSuccess ? onSuccess(result.Value!) : Problem(result.Error!.Value);

    public static IResult Problem(Error error)
    {
        var status = error.Type switch
        {
            ErrorType.Validation => StatusCodes.Status400BadRequest,
            ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
            ErrorType.Forbidden => StatusCodes.Status403Forbidden,
            ErrorType.NotFound => StatusCodes.Status404NotFound,
            ErrorType.Conflict => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status500InternalServerError
        };

        if (error.Type == ErrorType.Validation && error.Details is not null)
            return Results.ValidationProblem(
                error.Details,
                detail: error.Message,
                type: TypeBase + error.Code,
                statusCode: status);

        return Results.Problem(
            detail: error.Message,
            statusCode: status,
            type: TypeBase + error.Code,
            title: error.Code);
    }
}
