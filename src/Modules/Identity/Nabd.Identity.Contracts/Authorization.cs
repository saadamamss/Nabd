namespace Nabd.Identity.Contracts;

public static class Operations
{
    public const string TenantRead = "tenant.read";
    public const string UserRead = "user.read";
    public const string UserCreate = "user.create";
    public const string UserDelete = "user.delete";
    public const string UserRoleSet = "user.role.set";
    public const string UserLocaleSet = "user.locale.set";
    public const string PasswordChange = "auth.password.change";
    public const string UserLocaleSetOthers = "user.locale.set.others";
    public const string DeviceRegister = "device.register";
    public const string DeviceRead = "device.read";
    // Spec 06 (FR-61): the fleet page — the ONLY new ops this spec adds,
    // and the matrix grants them to platform_admin alone.
    public const string TenantList = "tenant.list";
    public const string TenantDisable = "tenant.disable";
    public const string TenantEnable = "tenant.enable";
}


public static class AuthorizationMatrix
{
    private static readonly Dictionary<string, IReadOnlySet<string>> Rows = new()
    {
        ["viewer"] = new HashSet<string>
        {
            Operations.TenantRead, Operations.UserRead,
            Operations.UserLocaleSet,Operations.PasswordChange,
            Operations.DeviceRead
        },

        ["tenant_admin"] = new HashSet<string>
        {
            Operations.TenantRead,Operations.UserRead,
            Operations.UserCreate, Operations.UserDelete,
            Operations.UserRoleSet, Operations.UserLocaleSet,
            Operations.PasswordChange, Operations.UserLocaleSetOthers,
            Operations.DeviceRegister, Operations.DeviceRead
        },
        ["platform_admin"] = new HashSet<string>
        {
            Operations.TenantRead, Operations.UserRead,
            Operations.UserCreate, Operations.UserDelete,
            Operations.UserRoleSet, Operations.UserLocaleSet,
            Operations.PasswordChange,Operations.UserLocaleSetOthers,
            Operations.DeviceRegister, Operations.DeviceRead,
            Operations.TenantList, Operations.TenantDisable, Operations.TenantEnable
        },
    };

    public static bool Allows(string roleCode, string operation) =>
        Rows.TryGetValue(roleCode, out var ops) && ops.Contains(operation);
}

public static class PermissionMatrix
{
    public static string? Assert(CallerContext acting, string operation, Guid targetTenantId)
    {
        var role = acting.RoleCodes.FirstOrDefault(r => AuthorizationMatrix.Allows(r, operation));
        if (role is null)
        {
            return IdentityErrors.Forbidden;
        }

        if (acting.TenantId != targetTenantId && role != "platform_admin")
        {
            return IdentityErrors.CrossTenantTarget;
        }
        return null;
    }

    public static string? AssertSelf (CallerContext acting, string operation, Guid tenantId, Guid targetUserId)
    {
        var error = Assert(acting, operation, tenantId);
        if (error is not null) return error;
        return acting.UserId == targetUserId ? null : IdentityErrors.Forbidden;
    }
}