using RetailFlow.Domain.Platform;

namespace RetailFlow.Domain.Identity;

/// <summary>
/// A named set of permissions inside one organization. Roles are per tenant even when they come
/// from a system template, so a shop can rename "Cashier" or take a permission away without
/// affecting anyone else (ADR-0013).
/// </summary>
public sealed class Role : TenantEntity
{
    private Role()
    {
    }

    private Role(Guid id, Guid organizationId, string name, string? description, bool isSystem)
        : base(id, organizationId)
    {
        Name = name;
        Description = description;
        IsSystem = isSystem;
    }

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    /// <summary>
    /// True for roles created from a template. They may be edited, but not deleted, because users
    /// and audit history reference them.
    /// </summary>
    public bool IsSystem { get; private set; }

    public static Role Create(Guid id, Guid organizationId, string name, string? description = null, bool isSystem = false)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Role name is required.", nameof(name));
        }

        return new Role(id, organizationId, name.Trim(), description?.Trim(), isSystem);
    }

    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Role name is required.", nameof(name));
        }

        Name = name.Trim();
    }

    public void Describe(string? description) => Description = description?.Trim();
}

/// <summary>
/// A permission code the software recognises, seeded from <see cref="Permissions.All"/>. Rows are
/// global rather than per tenant: the catalog is part of the application, not of a shop's data.
/// </summary>
public sealed class Permission
{
    private Permission()
    {
    }

    public int Id { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public string Module { get; private set; } = string.Empty;

    public string Description { get; private set; } = string.Empty;

    public static Permission Create(int id, string code, string module, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(module);

        return new Permission
        {
            Id = id,
            Code = code,
            Module = module,
            Description = description ?? string.Empty,
        };
    }

    public void Describe(string module, string description)
    {
        Module = module;
        Description = description ?? string.Empty;
    }
}

/// <summary>Grants a permission to a role.</summary>
public sealed class RolePermission : TenantEntity
{
    private RolePermission()
    {
    }

    private RolePermission(Guid id, Guid organizationId, Guid roleId, string permissionCode)
        : base(id, organizationId)
    {
        RoleId = roleId;
        PermissionCode = permissionCode;
    }

    public Guid RoleId { get; private set; }

    /// <summary>
    /// The permission code rather than its numeric id: the code is the stable contract, and storing
    /// it means a re-seed cannot silently repoint a grant at a different permission.
    /// </summary>
    public string PermissionCode { get; private set; } = string.Empty;

    public static RolePermission Create(Guid id, Guid organizationId, Guid roleId, string permissionCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permissionCode);

        if (!Permissions.Exists(permissionCode))
        {
            throw new DomainException($"'{permissionCode}' is not a known permission.");
        }

        return new RolePermission(id, organizationId, roleId, permissionCode);
    }
}

/// <summary>Assigns a role to a user.</summary>
public sealed class UserRole : TenantEntity
{
    private UserRole()
    {
    }

    private UserRole(Guid id, Guid organizationId, Guid userId, Guid roleId)
        : base(id, organizationId)
    {
        UserId = userId;
        RoleId = roleId;
    }

    public Guid UserId { get; private set; }

    public Guid RoleId { get; private set; }

    public static UserRole Create(Guid id, Guid organizationId, Guid userId, Guid roleId) =>
        new(id, organizationId, userId, roleId);
}

/// <summary>
/// Grants a user access to a branch, optionally with a different role there (FR-TEN-03). A
/// supervisor may be Manager in one outlet and Cashier in another, and the POS must enforce that
/// per branch rather than per account.
/// </summary>
public sealed class UserBranch : TenantEntity
{
    private UserBranch()
    {
    }

    private UserBranch(Guid id, Guid organizationId, Guid userId, Guid branchId, Guid? roleId, bool isDefault)
        : base(id, organizationId)
    {
        UserId = userId;
        BranchId = branchId;
        RoleId = roleId;
        IsDefault = isDefault;
    }

    public Guid UserId { get; private set; }

    public Guid BranchId { get; private set; }

    /// <summary>Role that applies in this branch only. Null means the account's roles apply.</summary>
    public Guid? RoleId { get; private set; }

    /// <summary>The branch selected when the user signs in without naming one.</summary>
    public bool IsDefault { get; private set; }

    public static UserBranch Create(
        Guid id,
        Guid organizationId,
        Guid userId,
        Guid branchId,
        Guid? roleId = null,
        bool isDefault = false) =>
        new(id, organizationId, userId, branchId, roleId, isDefault);

    public void SetRole(Guid? roleId) => RoleId = roleId;

    public void MakeDefault() => IsDefault = true;

    public void ClearDefault() => IsDefault = false;
}

/// <summary>
/// Grants or denies one permission for a user in one branch, on top of their roles. Exceptions are
/// explicit and audited, which beats cloning a role for every special case.
/// </summary>
public sealed class UserBranchPermission : TenantEntity
{
    private UserBranchPermission()
    {
    }

    private UserBranchPermission(
        Guid id,
        Guid organizationId,
        Guid userId,
        Guid branchId,
        string permissionCode,
        bool isGranted,
        string? reason)
        : base(id, organizationId)
    {
        UserId = userId;
        BranchId = branchId;
        PermissionCode = permissionCode;
        IsGranted = isGranted;
        Reason = reason;
    }

    public Guid UserId { get; private set; }

    public Guid BranchId { get; private set; }

    public string PermissionCode { get; private set; } = string.Empty;

    /// <summary>True grants the permission, false denies it. A denial always wins (ADR-0013).</summary>
    public bool IsGranted { get; private set; }

    public string? Reason { get; private set; }

    public static UserBranchPermission Create(
        Guid id,
        Guid organizationId,
        Guid userId,
        Guid branchId,
        string permissionCode,
        bool isGranted,
        string? reason = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permissionCode);

        if (!Permissions.Exists(permissionCode))
        {
            throw new DomainException($"'{permissionCode}' is not a known permission.");
        }

        return new UserBranchPermission(id, organizationId, userId, branchId, permissionCode, isGranted, reason?.Trim());
    }
}
