using System.Security.Claims;
using Mazda.Application.Common.Abstractions;

namespace Mazda.Api.Auth;

public class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    // Per-async-flow impersonation for background tasks that have lost their
    // HttpContext. Used by the MCP legacy-SSE transport: the POST endpoint
    // dispatches the JSON-RPC message in a fire-and-forget task (so the POST
    // returns 202 immediately and a long-running tool call can surface on the
    // SSE stream instead of blocking the POST), but the task no longer has an
    // HttpContext to read the JWT from — so it impersonates the user captured
    // before dispatching.
    private static readonly AsyncLocal<Guid?> _impersonatedUserId = new();

    public static IDisposable Impersonate(Guid userId)
    {
        var previous = _impersonatedUserId.Value;
        _impersonatedUserId.Value = userId;
        return new Restore(previous);
    }

    private sealed class Restore(Guid? previous) : IDisposable
    {
        public void Dispose() => _impersonatedUserId.Value = previous;
    }

    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public Guid? UserId
    {
        get
        {
            if (_impersonatedUserId.Value is { } impersonated) return impersonated;
            var sub = Principal?.FindFirstValue("sub") ?? Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(sub, out var g) ? g : null;
        }
    }

    public string? Email => Principal?.FindFirstValue("email") ?? Principal?.FindFirstValue(ClaimTypes.Email);
    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated ?? _impersonatedUserId.Value is not null;
}
