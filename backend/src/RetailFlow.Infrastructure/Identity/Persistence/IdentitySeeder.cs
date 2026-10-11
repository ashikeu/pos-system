using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RetailFlow.Application.Identity.Abstractions;
using RetailFlow.Application.Platform.Abstractions;
using RetailFlow.Domain.Identity;
using RetailFlow.Infrastructure.Platform.Persistence;

namespace RetailFlow.Infrastructure.Identity.Persistence;

/// <summary>
/// Creates the role templates for an organization and, in development, an owner account to sign in
/// with. Phase 4 calls <see cref="CreateSystemRolesAsync"/> from real organization provisioning;
/// the demo account exists only for local work.
/// </summary>
public sealed class IdentitySeeder
{
    /// <summary>Stable id of the demo owner, so a developer can reference it directly.</summary>
    public static readonly Guid DemoOwnerUserId = new("0193f000-0000-7000-8000-000000000003");

    public const string DemoOwnerEmail = "owner@demo.retailflow.local";

    /// <summary>
    /// Development-only password. It is deliberately obvious: a weak-looking value in source is
    /// safer than one that might be mistaken for a production credential (ADR-0023).
    /// </summary>
    public const string DemoOwnerPassword = "DemoOwner!2026";

    private readonly RetailFlowDbContext _context;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IMutableTenantContext _tenant;
    private readonly IClock _clock;
    private readonly IGuidGenerator _guids;
    private readonly ILogger<IdentitySeeder> _logger;

    public IdentitySeeder(
        RetailFlowDbContext context,
        IPasswordHasher passwordHasher,
        IMutableTenantContext tenant,
        IClock clock,
        IGuidGenerator guids,
        ILogger<IdentitySeeder> logger)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(passwordHasher);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(guids);
        ArgumentNullException.ThrowIfNull(logger);

        _context = context;
        _passwordHasher = passwordHasher;
        _tenant = tenant;
        _clock = clock;
        _guids = guids;
        _logger = logger;
    }

    /// <summary>
    /// Copies the system role templates into an organization, adding any that are missing. Roles
    /// are per tenant so a shop can edit them (ADR-0013); re-running this does not undo those edits,
    /// it only fills in roles that were never created.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, Guid>> CreateSystemRolesAsync(
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
            IdentitySeedLog.RolesCreated(_logger, created, organizationId);
        }

        return result;
    }

    /// <summary>
    /// Creates the demo owner account and grants it the demo branch. Development only: production
    /// accounts are created through organization provisioning with a password the owner sets.
    /// </summary>
    public async Task SeedDemoOwnerAsync(
        Guid organizationId,
        Guid branchId,
        CancellationToken cancellationToken = default)
    {
        _tenant.Set(new TenantScope(organizationId));

        try
        {
            var roles = await CreateSystemRolesAsync(organizationId, cancellationToken).ConfigureAwait(false);

            var exists = await _context.Users
                .IgnoreQueryFilters()
                .AnyAsync(u => u.Id == DemoOwnerUserId, cancellationToken)
                .ConfigureAwait(false);

            if (exists)
            {
                return;
            }

            var user = User.Create(
                DemoOwnerUserId,
                organizationId,
                DemoOwnerEmail,
                "Demo Owner",
                _passwordHasher.Hash(DemoOwnerPassword));

            user.ConfirmEmail();
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
            IdentitySeedLog.DemoOwnerCreated(_logger, DemoOwnerEmail);
        }
        finally
        {
            _tenant.Clear();
        }
    }
}

internal static partial class IdentitySeedLog
{
    [LoggerMessage(
        EventId = 6300,
        Level = LogLevel.Information,
        Message = "Created {Count} system role(s) for organization {OrganizationId}")]
    public static partial void RolesCreated(ILogger logger, int count, Guid organizationId);

    [LoggerMessage(
        EventId = 6301,
        Level = LogLevel.Warning,
        Message = "Seeded the development owner account {Email}. Development only — never run this against production data.")]
    public static partial void DemoOwnerCreated(ILogger logger, string email);
}
