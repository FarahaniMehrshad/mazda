using System.Security.Claims;
using Mazda.Application.Common.Abstractions;

namespace Mazda.Api.Auth;

public class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public Guid? UserId
    {
        get
        {
            var sub = Principal?.FindFirstValue("sub") ?? Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(sub, out var g) ? g : null;
        }
    }

    public string? Email => Principal?.FindFirstValue("email") ?? Principal?.FindFirstValue(ClaimTypes.Email);
    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated ?? false;
}
