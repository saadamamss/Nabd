using System.Security.Claims;
using Nabd.Identity.Contracts;

namespace Nabd.ApiHost.Auth;

public static class Caller
{
    public static CallerContext? FromUser(ClaimsPrincipal user)
    {
        var sub = user.FindFirst("sub")?.Value;
        var tid = user.FindFirst("tid")?.Value;
        var roles = user.FindAll("roles").Select(c => c.Value).ToArray();

        if (sub is null || tid is null || roles.Length == 0
            || !Guid.TryParse(sub, out var userId)
            || !Guid.TryParse(tid, out var tenantId))
            return null;   // fail-shut: a malformed claim set never becomes a caller

        return new CallerContext(userId, tenantId, roles);
    }
}