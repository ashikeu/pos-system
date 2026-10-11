using Microsoft.AspNetCore.Authorization;
using RetailFlow.Application.Identity.Abstractions;
using RetailFlow.Application.Platform.Abstractions;

namespace RetailFlow.Api.Authorization;

/// <summary>
/// Requires a permission on an endpoint (ADR-0013). One attribute and one handler for the whole
/// API: there are no bespoke <c>if (role == "Admin")</c> checks anywhere, because those are how an
/// endpoint ends up with subtly different rules from its neighbours.
/// </summary>
/// <example><c>[HasPermission(Permissions.Sales.Void)]</c></example>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class HasPermissionAttribute : AuthorizeAttribute
{
    public const string PolicyPrefix = "perm:";

    public HasPermissionAttribute(string permission)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);

        Permission = permission;
        Policy = PolicyPrefix + permission;
    }

    public string Permission { get; }
}

/// <summary>The requirement a <see cref="HasPermissionAttribute"/> policy carries.</summary>
public sealed class PermissionRequirement : IAuthorizationRequirement
{
    public PermissionRequirement(string permission)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);
        Permission = permission;
    }

    public string Permission { get; }
}

/// <summary>
/// Builds a policy on demand for any <c>perm:</c> name, so adding a permission needs no
/// registration step — the attribute is the registration.
/// </summary>
public sealed class PermissionPolicyProvider : IAuthorizationPolicyProvider
{
    private readonly DefaultAuthorizationPolicyProvider _fallback;

    public PermissionPolicyProvider(Microsoft.Extensions.Options.IOptions<AuthorizationOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _fallback = new DefaultAuthorizationPolicyProvider(options);
    }

    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _fallback.GetDefaultPolicyAsync();

    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => _fallback.GetFallbackPolicyAsync();

    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        ArgumentNullException.ThrowIfNull(policyName);

        if (!policyName.StartsWith(HasPermissionAttribute.PolicyPrefix, StringComparison.Ordinal))
        {
            return _fallback.GetPolicyAsync(policyName);
        }

        var permission = policyName[HasPermissionAttribute.PolicyPrefix.Length..];

        var policy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(new PermissionRequirement(permission))
            .Build();

        return Task.FromResult<AuthorizationPolicy?>(policy);
    }
}

/// <summary>
/// Decides a permission requirement by resolving the user's effective permissions for the branch in
/// scope — never from claims in the token (ADR-0027). A client that edits its own token cannot
/// grant itself anything, because the answer comes from the database.
/// </summary>
public sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    private readonly IPermissionResolver _permissions;
    private readonly ITenantContext _tenant;
    private readonly ICurrentUser _currentUser;

    public PermissionAuthorizationHandler(
        IPermissionResolver permissions,
        ITenantContext tenant,
        ICurrentUser currentUser)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(currentUser);

        _permissions = permissions;
        _tenant = tenant;
        _currentUser = currentUser;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        if (_currentUser.UserId is not { } userId || !_tenant.HasTenant)
        {
            // No authenticated principal, or authentication did not establish a tenant: nothing to
            // resolve against, so the requirement is simply not met.
            return;
        }

        var set = await _permissions.ResolveAsync(userId, _tenant.BranchId).ConfigureAwait(false);

        if (set.Allows(requirement.Permission))
        {
            context.Succeed(requirement);
        }
    }
}
