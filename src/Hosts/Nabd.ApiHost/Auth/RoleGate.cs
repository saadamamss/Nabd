using Microsoft.AspNetCore.Authorization;

namespace Nabd.ApiHost.Auth;

public static class Policies
{
    public const string Viewer = "viewer";
    public const string TenantAdmin = "tenant_admin";
    public const string PlatformAdmin = "platform_admin";
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
        };

    public static void Register(AuthorizationOptions options)
    {
        foreach (var (policy, roles) in Admits)
            options.AddPolicy(policy, p => p.RequireRole(roles));

        // deny-by-default (plan/07 §5): any route WITHOUT an explicit wall at
        // least demands authentication. [AllowAnonymous] doors are exempt —
        // the anonymous club stays exactly {Login, Logout, Refresh, Register}.
        options.FallbackPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build();
    }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class ViewerAttribute(string operation) : AuthorizeAttribute(Policies.Viewer) {
    public string Operation { get; } = operation;
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class TenantAdminAttribute(string operation) : AuthorizeAttribute(Policies.TenantAdmin) {
    public string Operation { get; } = operation;
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class PlatformAdminAttribute(string operation) : AuthorizeAttribute(Policies.PlatformAdmin) {
    public string Operation { get; } = operation;   
}
