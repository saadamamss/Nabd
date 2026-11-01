namespace Nabd.ApiHost.Auth;

[AttributeUsage(AttributeTargets.Method)]
public sealed class ViewerAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Method)]
public sealed class TenantAdminAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Method)]
public sealed class PlatformAdminAttribute : Attribute { }
