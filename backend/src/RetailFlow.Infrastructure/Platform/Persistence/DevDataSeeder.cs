using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RetailFlow.Application.Platform.Abstractions;
using RetailFlow.Domain.Tenancy;
using RetailFlow.Infrastructure.Identity.Persistence;
using RetailFlow.Infrastructure.Tenancy.Persistence;

namespace RetailFlow.Infrastructure.Platform.Persistence;

/// <summary>
/// Creates a predictable demo tenant for local development (Phase 2). Identifiers are fixed, so a
/// developer can hard-code them in a request while trying something out and re-running the seeder
/// changes nothing.
/// </summary>
/// <remarks>
/// The tenant it creates has the same shape as a provisioned one — settings, a branch with a
/// default warehouse, a terminal and a subscription — so local work exercises the real
/// configuration chain. Catalog data arrives with Phase 5. Roles and the demo owner account are
/// created by <see cref="IdentitySeeder"/>, which this seeder calls once the tenant exists.
/// </remarks>
public sealed class DevDataSeeder
{
    /// <summary>Stable id of the demo organization: <c>Demo Retail</c>.</summary>
    public static readonly Guid DemoOrganizationId = new("0193f000-0000-7000-8000-000000000001");

    /// <summary>Stable id of the demo main branch.</summary>
    public static readonly Guid DemoBranchId = new("0193f000-0000-7000-8000-000000000002");

    /// <summary>Stable id of the demo branch's default warehouse.</summary>
    public static readonly Guid DemoWarehouseId = new("0193f000-0000-7000-8000-000000000004");

    /// <summary>Stable id of the demo terminal.</summary>
    public static readonly Guid DemoTerminalId = new("0193f000-0000-7000-8000-000000000005");

    private readonly RetailFlowDbContext _context;
    private readonly IMutableTenantContext _tenant;
    private readonly IdentitySeeder _identitySeeder;
    private readonly SubscriptionPlanSeeder _planSeeder;
    private readonly IGuidGenerator _guids;
    private readonly IClock _clock;
    private readonly ILogger<DevDataSeeder> _logger;

    public DevDataSeeder(
        RetailFlowDbContext context,
        IMutableTenantContext tenant,
        IdentitySeeder identitySeeder,
        SubscriptionPlanSeeder planSeeder,
        IGuidGenerator guids,
        IClock clock,
        ILogger<DevDataSeeder> logger)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(identitySeeder);
        ArgumentNullException.ThrowIfNull(planSeeder);
        ArgumentNullException.ThrowIfNull(guids);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _context = context;
        _tenant = tenant;
        _identitySeeder = identitySeeder;
        _planSeeder = planSeeder;
        _guids = guids;
        _clock = clock;
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
                _context.BranchSettings.Add(
                    BranchSettings.CreateDefault(_guids.NewId(), DemoOrganizationId, DemoBranchId));
                await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                SeedLog.BranchCreated(_logger, DemoBranchId);
            }

            await SeedOperatingShapeAsync(cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Adds the rows that make the demo tenant usable rather than merely present: its settings row,
    /// a default warehouse, a till, and a subscription on a plan.
    /// </summary>
    private async Task SeedOperatingShapeAsync(CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var changed = false;

        var hasSettings = await _context.OrganizationSettings
            .IgnoreQueryFilters()
            .AnyAsync(s => s.OrganizationId == DemoOrganizationId, cancellationToken)
            .ConfigureAwait(false);

        if (!hasSettings)
        {
            // Left entirely on the platform defaults, so changing one locally shows the resolution
            // chain doing its work (FR-TEN-04).
            _context.OrganizationSettings.Add(
                OrganizationSettings.CreateDefault(_guids.NewId(), DemoOrganizationId));
            changed = true;
        }

        var hasWarehouse = await _context.Warehouses
            .IgnoreQueryFilters()
            .AnyAsync(w => w.Id == DemoWarehouseId, cancellationToken)
            .ConfigureAwait(false);

        if (!hasWarehouse)
        {
            _context.Warehouses.Add(Warehouse.Create(
                DemoWarehouseId,
                DemoOrganizationId,
                DemoBranchId,
                "MAIN",
                "Demo Main Store",
                WarehouseType.Main,
                isDefault: true));
            changed = true;
        }

        var hasTerminal = await _context.PosTerminals
            .IgnoreQueryFilters()
            .AnyAsync(t => t.Id == DemoTerminalId, cancellationToken)
            .ConfigureAwait(false);

        if (!hasTerminal)
        {
            var terminal = PosTerminal.Create(
                DemoTerminalId,
                DemoOrganizationId,
                DemoBranchId,
                "POS1",
                "Demo Counter 1");
            terminal.UpdateDetails(
                terminal.Name,
                DemoWarehouseId,
                receiptTemplate58: null,
                receiptTemplate80: null);

            _context.PosTerminals.Add(terminal);
            changed = true;
        }

        var hasSubscription = await _context.OrganizationSubscriptions
            .IgnoreQueryFilters()
            .AnyAsync(s => s.OrganizationId == DemoOrganizationId, cancellationToken)
            .ConfigureAwait(false);

        if (!hasSubscription)
        {
            // Plans are platform data, so make sure the catalog exists before pointing at it.
            await _planSeeder.SyncAsync(cancellationToken).ConfigureAwait(false);

            _context.OrganizationSubscriptions.Add(OrganizationSubscription.Start(
                _guids.NewId(now),
                DemoOrganizationId,
                SubscriptionPlanSeeder.StandardPlanId,
                DateOnly.FromDateTime(now.UtcDateTime),
                periodDays: 365,
                trialDays: 0));
            changed = true;
        }

        if (changed)
        {
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            SeedLog.OperatingShapeCreated(_logger, DemoOrganizationId);
        }
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

    [LoggerMessage(
        EventId = 5002,
        Level = LogLevel.Information,
        Message = "Seeded demo settings, warehouse, terminal and subscription for {OrganizationId}")]
    public static partial void OperatingShapeCreated(ILogger logger, Guid organizationId);
}
