namespace Nabd.ApiHost.Auth;

using System.Text.Json;
using Nabd.Identity.Contracts;

/// <summary>
/// The dev-caller lane (spec 02's crutch): identity from a header while
/// NABD_DEV_FAKE_AUTH=true. With ONE home by design — spec 04 executes the
/// dev-lane deletion drill against this file, and there is exactly one place.
/// </summary>
public sealed record DevCallerJson(Guid UserId, Guid TenantId, string[] RoleCodes);

public static class DevCaller
{
    /// <summary>MVC [FromBody] is case-insensitive by default (shop-law 18); the manual
    /// header parse is the case-SENSITIVE world — hence the explicit opts.</summary>
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public static CallerContext? Read(HttpRequest request)
    {
        if (Environment.GetEnvironmentVariable("NABD_DEV_FAKE_AUTH") != "true") return null;

        var header = request.Headers["X-Nabd-Dev-Caller"].FirstOrDefault();
        if (string.IsNullOrEmpty(header)) return null;

        try
        {
            var json = JsonSerializer.Deserialize<DevCallerJson>(header, JsonOpts);
            return json is null ? null : new CallerContext(json.UserId, json.TenantId, json.RoleCodes);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
