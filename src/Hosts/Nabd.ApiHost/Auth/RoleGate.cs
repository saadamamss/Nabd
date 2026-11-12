using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Nabd.ApiHost.Auth;

public static class Policies
{
    public const string Viewer = "viewer";
    public const string TenantAdmin = "tenant_admin";
    public const string PlatformAdmin = "platform_admin";
    public const string Internal = "internal";
}

// P1 spec 04 — THE policy registry (one definition, law #27): Program registers
// from here, and RouteWallTests reads THIS table instead of its own copy — a
// test-side duplicate could drift silently; a shared table cannot.
// The ladder is wide→narrow: viewer (all roles) → tenant_admin (two) →
// platform_admin (one). Wide doors only ever grant operations the matrix
// double-checks in the service (RouteWallTests drift detector).
public static class PolicyBook
{
    /// <summary>policy name → the roles whose JWT makes the door pass.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Admits =
        new Dictionary<string, string[]>
        {
            [Policies.Viewer] = ["viewer", "tenant_admin", "platform_admin"],
            [Policies.TenantAdmin] = ["tenant_admin", "platform_admin"],
            [Policies.PlatformAdmin] = ["platform_admin"],
            [Policies.Internal] = []
        };

    public static void Register(AuthorizationOptions options)
    {
        foreach (var (policy, roles) in Admits)
        {
            if(roles.Length == 0) continue;
            options.AddPolicy(policy, p => p.RequireRole(roles));
        }

        // deny-by-default (plan/07 §5): any route WITHOUT an explicit wall at
        // least demands authentication. [AllowAnonymous] doors are exempt —
        // the anonymous club stays exactly {Login, Logout, Refresh, Register}.
        options.FallbackPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build();

        // the internal wall: a requirement, not a role — the handler decides
        options.AddPolicy(Policies.Internal, p => p.AddRequirements(new InternalRequirement()));
    }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class ViewerAttribute(string operation) : AuthorizeAttribute(Policies.Viewer)
{
    public string Operation { get; } = operation;
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class TenantAdminAttribute(string operation) : AuthorizeAttribute(Policies.TenantAdmin)
{
    public string Operation { get; } = operation;
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class PlatformAdminAttribute(string operation) : AuthorizeAttribute(Policies.PlatformAdmin)
{
    public string Operation { get; } = operation;
}


// P2 spec 05 — the shared secret, read ONCE at startup, fail-shut: unconfigured
// = the app refuses to boot (never half-boots with open broker doors).
public sealed class InternalSecrets
{
    public byte[] Secret { get; }

    public InternalSecrets()
    {
        var secret = Environment.GetEnvironmentVariable("NABD_INTERNAL_SHARED_SECRET")
            ?? throw new InvalidOperationException(
                "NABD_INTERNAL_SHARED_SECRET must be set (fail-shut: the internal doors refuse to exist without it, NFR-08).");
        Secret = Encoding.UTF8.GetBytes(secret);
    }
}

public sealed class InternalRequirement : IAuthorizationRequirement { }

/// <summary>The requirement's judge: constant-time compare of the ?k= query value
/// against the startup secret. EMQX 5 carries the secret in the URL (dev; P6 revisits
/// with TLS — header names with dashes are unreachable through EMQX's env config).</summary>
public sealed class InternalSecretHandler(InternalSecrets secrets, IHttpContextAccessor accessor)
    : AuthorizationHandler<InternalRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, InternalRequirement requirement)
    {
        var provided = accessor.HttpContext?.Request.Query["k"].ToString();
        var ok = !string.IsNullOrEmpty(provided)
                 && CryptographicOperations.FixedTimeEquals(
                     Encoding.UTF8.GetBytes(provided), secrets.Secret);
        if (ok) context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class InternalAttribute() : AuthorizeAttribute(Policies.Internal)
{
}

// P2 spec 05 — EMQX 5.x reads 4xx/5xx as "ignore" and continues the chain; a
// policy 401 is safe ONLY while allow_anonymous=false also holds. The doors'
// own contract is stronger: ALWAYS 200 with an explicit result. This handler
// intercepts the default 401 challenge for internal doors ONLY — every other
// policy keeps the standard middleware behavior untouched.
public sealed class InternalDenyResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(
        RequestDelegate next, HttpContext context,
        AuthorizationPolicy policy, PolicyAuthorizationResult evaluation)
    {
        if (!evaluation.Succeeded
            && context.GetEndpoint()?.Metadata.GetMetadata<InternalAttribute>() is not null)
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            await context.Response.WriteAsJsonAsync(new { result = "deny" });
            return;
        }

        await _default.HandleAsync(next, context, policy, evaluation);
    }
}