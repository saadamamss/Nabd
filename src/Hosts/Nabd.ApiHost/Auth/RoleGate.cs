using Microsoft.AspNetCore.Authorization;

namespace Nabd.ApiHost.Auth;

public static class Policies
{
    public const string Viewer = "viewer";
    public const string TenantAdmin = "tenant_admin";
    public const string PlatformAdmin = "platform_admin";
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
