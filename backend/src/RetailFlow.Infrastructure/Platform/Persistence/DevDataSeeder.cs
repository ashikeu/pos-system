using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RetailFlow.Application.Platform.Abstractions;
using RetailFlow.Domain.Tenancy;
using RetailFlow.Infrastructure.Identity.Persistence;

namespace RetailFlow.Infrastructure.Platform.Persistence;

/// <summary>
/// Creates a predictable demo tenant for local development (Phase 2). Identifiers are fixed, so a
/// developer can hard-code them in a request while trying something out and re-running the seeder
/// changes nothing.
/// </summary>
/// <remarks>
/// Terminals, warehouses and catalog data arrive with the phases that own those entities
/// (Phase 4 and Phase 5). Roles and the demo owner account are created by
/// <see cref="IdentitySeeder"/>, which this seeder calls once the tenant exists.
/// </remarks>
public sealed class DevDataSeeder
{
    /// <summary>Stable id of the demo organization: <c>Demo Retail</c>.</summary>
    public static readonly Guid DemoOrganizationId = new("0193f000-0000-7000-8000-000000000001");

    /// <summary>Stable id of the demo main branch.</summary>
    public static readonly Guid DemoBranchId = new("0193f000-0000-7000-8000-000000000002");

    private readonly RetailFlowDbContext _context;
    private readonly IMutableTenantContext _tenant;
    private readonly IdentitySeeder _identitySeeder;
    private readonly ILogger<DevDataSeeder> _logger;

    public DevDataSeeder(
        RetailFlowDbContext context,
        IMutableTenantContext tenant,
        IdentitySeeder identitySeeder,
        ILogger<DevDataSeeder> logger)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(identitySeeder);
        ArgumentNullException.ThrowIfNull(logger);

        _context = context;
        _tenant = tenant;
        _identitySeeder = identitySeeder;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        // The seeder writes tenant-owned rows, so it has to act as that tenant like any other
        // caller: the save interceptor does not make exceptions for seed data (ADR-0004).
        _tenant.Set(new TenantScope(DemoOrganizationId));

        try
        {
            var organizationExists = await _context.Organizations
                .IgnoreQueryFilters()
                .AnyAsync(o => o.Id == DemoOrganizationId, cancellationToken)
                .ConfigureAwait(false);

            if (!organizationExists)
            {
                var organization = Organization.Create(
                    DemoOrganizationId,
                    "DEMO",
                    "Demo Retail",
                    timeZoneId: "Asia/Dhaka");
                organization.SetLegalIdentity("Demo Retail Limited", binNumber: null);

                _context.Organizations.Add(organization);
                await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                SeedLog.OrganizationCreated(_logger, DemoOrganizationId);
            }

            var branchExists = await _context.Branches
                .IgnoreQueryFilters()
                .AnyAsync(b => b.Id == DemoBranchId, cancellationToken)
                .ConfigureAwait(false);

            if (!branchExists)
            {
                var branch = Branch.Create(
                    DemoBranchId,
                    DemoOrganizationId,
                    "MAIN",
                    "Demo Main Outlet",
                    "Asia/Dhaka",
                    TimeOnly.MinValue);
                branch.UpdateDetails(
                    "Demo Main Outlet",
                    "ডেমো মেইন আউটলেট",
                    "123 Satmasjid Road",
                    "Dhaka",
                    "+8801700000000");

                _context.Branches.Add(branch);
                await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                SeedLog.BranchCreated(_logger, DemoBranchId);
            }
        }
        finally
        {
            _tenant.Clear();
        }

        // Roles and the owner account come last: they reference the organization and branch above.
        await _identitySeeder
            .SeedDemoOwnerAsync(DemoOrganizationId, DemoBranchId, cancellationToken)
            .ConfigureAwait(false);
    }
}

internal static partial class SeedLog
{
    [LoggerMessage(
        EventId = 5000,
        Level = LogLevel.Information,
        Message = "Seeded demo organization {OrganizationId}")]
    public static partial void OrganizationCreated(ILogger logger, Guid organizationId);

    [LoggerMessage(
        EventId = 5001,
        Level = LogLevel.Information,
        Message = "Seeded demo branch {BranchId}")]
    public static partial void BranchCreated(ILogger logger, Guid branchId);
}
