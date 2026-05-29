using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Mazda.Domain.Common;

namespace Mazda.Api.Endpoints;

public static class ResultExtensions
{
    public static IResult ToHttp<T>(this Result<T> result) =>
        result.IsSuccess
            ? Results.Ok(result.Value)
            : ToError(result.Error);

    public static IResult ToHttp(this Result result) =>
        result.IsSuccess ? Results.NoContent() : ToError(result.Error);

    private static IResult ToError(Error error) => error.Code switch
    {
        "NotFound"   => Results.NotFound(new { error = error.Message }),
        "Validation" => Results.BadRequest(new { error = error.Message }),
        "Conflict"   => Results.Conflict(new { error = error.Message }),
        "External"   => Results.Problem(error.Message, statusCode: StatusCodes.Status502BadGateway),
        _            => Results.Problem(error.Message)
    };
}
