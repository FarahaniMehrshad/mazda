using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;

namespace Mazda.Api.Middleware;

public class ValidationExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext ctx, Exception ex, CancellationToken ct)
    {
        if (ex is not ValidationException vex) return false;
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsJsonAsync(new
        {
            error = "Validation failed",
            details = vex.Errors.Select(e => new { e.PropertyName, e.ErrorMessage })
        }, ct);
        return true;
    }
}
