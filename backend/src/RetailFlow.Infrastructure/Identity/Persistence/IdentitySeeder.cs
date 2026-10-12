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
    private readonly IIdentityProvisioning _provisioning;
    private readonly IMutableTenantContext _tenant;
    private readonly ILogger<IdentitySeeder> _logger;

    public IdentitySeeder(
        RetailFlowDbContext context,
        IIdentityProvisioning provisioning,
        IMutableTenantContext tenant,
        ILogger<IdentitySeeder> logger)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(provisioning);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(logger);

        _context = context;
        _provisioning = provisioning;
        _tenant = tenant;
        _logger = logger;
    }

    /// <summary>
    /// Copies the system role templates into an organization. Kept here as the seeding entry point;
    /// the behaviour lives in <see cref="IIdentityProvisioning"/> so provisioning a real tenant and
    /// seeding a development one cannot drift apart.
    /// </summary>
    public Task<IReadOnlyDictionary<string, Guid>> CreateSystemRolesAsync(
        Guid organizationId,
        CancellationToken cancellationToken = default) =>
        _provisioning.EnsureSystemRolesAsync(organizationId, cancellationToken);

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
            await CreateSystemRolesAsync(organizationId, cancellationToken).ConfigureAwait(false);

            var exists = await _context.Users
                .IgnoreQueryFilters()
                .AnyAsync(u => u.OrganizationId == organizationId && u.Email == DemoOwnerEmail, cancellationToken)
                .ConfigureAwait(false);

            if (exists)
            {
                return;
            }

            var created = await _provisioning
                .CreateOwnerAsync(
                    organizationId,
                    branchId,
                    new OwnerAccountRequest(
                        DemoOwnerEmail,
                        "Demo Owner",
                        DemoOwnerPassword,

                        // Development convenience: the demo account is used straight away in
                        // Swagger and by the integration suite, so it skips both ceremonies.
                        MustChangePassword: false,
                        EmailConfirmed: true,
                        UserId: DemoOwnerUserId),
                    cancellationToken)
                .ConfigureAwait(false);

            if (created.IsFailure)
            {
                throw new InvalidOperationException(
                    "Seeding the development owner failed: " + created.Error);
            }

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
        EventId = 6301,
        Level = LogLevel.Warning,
        Message = "Seeded the development owner account {Email}. Development only — never run this against production data.")]
    public static partial void DemoOwnerCreated(ILogger logger, string email);
}
