using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Mazda.Api.Middleware;

/// <summary>
/// Catch-all exception handler. Logs every unhandled exception to ILogger and,
/// in Development, surfaces the actual message + a short stack snippet in the
/// HTTP response body so the developer doesn't have to dig through container
/// logs for a 500. Registered AFTER ValidationExceptionHandler, so the 400
/// path keeps its existing shape.
/// </summary>
public class DevExceptionHandler(ILogger<DevExceptionHandler> log, IWebHostEnvironment env) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext ctx, Exception ex, CancellationToken ct)
    {
        // Log every unhandled exception with the route info so we can correlate
        // failed requests in the container logs.
        log.LogError(ex, "Unhandled exception on {Method} {Path}", ctx.Request.Method, ctx.Request.Path);

        ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
        ctx.Response.ContentType = "application/problem+json";

        // In Production we keep the response generic to avoid leaking internals.
        // In Development we include the exception type + message + first few
        // stack frames so the developer can see what blew up without leaving
        // the browser DevTools tab.
        object payload = env.IsDevelopment()
            ? new
            {
                type = "https://tools.ietf.org/html/rfc9110#section-15.6.1",
                title = "Unhandled exception",
                status = 500,
                detail = ex.Message,
                exceptionType = ex.GetType().FullName,
                stack = string.Join('\n', (ex.StackTrace ?? string.Empty)
                    .Split('\n').Take(20)),
                inner = ex.InnerException is { } inner
                    ? $"{inner.GetType().FullName}: {inner.Message}"
                    : null,
            }
            : new
            {
                type = "https://tools.ietf.org/html/rfc9110#section-15.6.1",
                title = "An error occurred while processing your request.",
                status = 500,
            };

        await ctx.Response.WriteAsJsonAsync(payload, ct);
        return true;
    }
}
