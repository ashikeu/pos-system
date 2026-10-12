using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RetailFlow.Application.Identity.Abstractions;
using RetailFlow.Application.Identity.UseCases;
using RetailFlow.Application.Platform.Abstractions;
using RetailFlow.Domain.Identity;
using RetailFlow.Domain.Platform;
using RetailFlow.Infrastructure.Platform.Persistence;

namespace RetailFlow.Infrastructure.Identity.Services;

/// <summary>
/// The identity side of creating an organization: its copy of the role templates and its first
/// user (ADR-0013). Used by tenancy provisioning and by the development seeder, so both paths
/// produce exactly the same shape of tenant.
/// </summary>
internal sealed class IdentityProvisioningService : IIdentityProvisioning
{
    private readonly RetailFlowDbContext _context;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IGuidGenerator _guids;
    private readonly AuthenticationOptions _options;
    private readonly ILogger<IdentityProvisioningService> _logger;

    public IdentityProvisioningService(
        RetailFlowDbContext context,
        IPasswordHasher passwordHasher,
        IGuidGenerator guids,
        AuthenticationOptions options,
        ILogger<IdentityProvisioningService> logger)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(passwordHasher);
        ArgumentNullException.ThrowIfNull(guids);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _context = context;
        _passwordHasher = passwordHasher;
        _guids = guids;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Copies the system role templates into an organization, adding any that are missing. Roles
    /// are per tenant so a shop can edit them (ADR-0013); re-running this does not undo those edits,
    /// it only fills in roles that were never created.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, Guid>> EnsureSystemRolesAsync(
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        var existing = await _context.Roles
            .IgnoreQueryFilters()
            .Where(r => r.OrganizationId == organizationId)
            .ToDictionaryAsync(r => r.Name, StringComparer.OrdinalIgnoreCase, cancellationToken)
            .ConfigureAwait(false);

        var result = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var created = 0;

        foreach (var template in SystemRoles.All)
        {
            if (existing.TryGetValue(template.Name, out var existingRole))
            {
                result[template.Name] = existingRole.Id;
                continue;
            }

            var role = Role.Create(
                _guids.NewId(),
                organizationId,
                template.Name,
                template.Description,
                isSystem: true);

            _context.Roles.Add(role);

            foreach (var code in template.PermissionCodes)
            {
                _context.RolePermissions.Add(
                    RolePermission.Create(_guids.NewId(), organizationId, role.Id, code));
            }

            result[template.Name] = role.Id;
            created++;
        }

        if (created > 0)
        {
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            IdentityProvisioningLog.RolesCreated(_logger, created, organizationId);
        }

        return result;
    }

    public async Task<Result<Guid>> CreateOwnerAsync(
        Guid organizationId,
        Guid branchId,
        OwnerAccountRequest owner,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        var policy = PasswordRules.Validate(owner.Password, _options.MinimumPasswordLength);
        if (policy.IsFailure)
        {
            return policy.Error;
        }

        string normalizedEmail;

        try
        {
            normalizedEmail = User.Normalize(owner.Email ?? string.Empty);
        }
        catch (ArgumentException ex)
        {
            return Error.Validation("identity.email_invalid", ex.Message);
        }

        var taken = await _context.Users
            .IgnoreQueryFilters()
            .AnyAsync(u => u.OrganizationId == organizationId && u.Email == normalizedEmail, cancellationToken)
            .ConfigureAwait(false);

        if (taken)
        {
            return Error.Conflict(
                "identity.email_taken",
                "An account with that email address already exists in this organization.");
        }

        var roles = await EnsureSystemRolesAsync(organizationId, cancellationToken).ConfigureAwait(false);

        // The initial password was chosen by whoever provisioned the tenant, not by its owner, so
        // by default the first sign-in has to replace it before anything else happens.
        var user = User.Create(
            owner.UserId ?? _guids.NewId(),
            organizationId,
            normalizedEmail,
            owner.FullName,
            _passwordHasher.Hash(owner.Password),
            owner.MustChangePassword);

        if (owner.EmailConfirmed)
        {
            user.ConfirmEmail();
        }

        _context.Users.Add(user);

        if (roles.TryGetValue(SystemRoles.Owner, out var ownerRoleId))
        {
            _context.UserRoles.Add(UserRole.Create(_guids.NewId(), organizationId, user.Id, ownerRoleId));
        }

        _context.UserBranches.Add(UserBranch.Create(
            _guids.NewId(),
            organizationId,
            user.Id,
            branchId,
            roleId: null,
            isDefault: true));

        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return user.Id;
    }

    public Task<int> CountActiveUsersAsync(Guid organizationId, CancellationToken cancellationToken = default) =>
        _context.Users
            .IgnoreQueryFilters()
            .CountAsync(u => u.OrganizationId == organizationId && u.IsActive, cancellationToken);
}

internal static partial class IdentityProvisioningLog
{
    [LoggerMessage(
        EventId = 6310,
        Level = LogLevel.Information,
        Message = "Created {Count} system role(s) for organization {OrganizationId}")]
    public static partial void RolesCreated(ILogger logger, int count, Guid organizationId);
}
