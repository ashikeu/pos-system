using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RetailFlow.Application.Platform.Abstractions;
using RetailFlow.Domain.Tenancy;
using RetailFlow.Infrastructure.Platform.Persistence;

namespace RetailFlow.Infrastructure.Tenancy.Persistence;

/// <summary>
/// Creates the plan catalog the platform ships with (FR-TEN-05). Plans are platform reference data
/// rather than tenant data, so they are seeded on start-up like the permission catalog is.
/// </summary>
/// <remarks>
/// Missing plans and missing features are added; existing rows are never overwritten. Prices and
/// limits are commercial decisions that whoever runs the platform may change in the database, and a
/// seeder that re-imposed the shipped values on every restart would quietly undo those changes.
/// </remarks>
public sealed class SubscriptionPlanSeeder
{
    /// <summary>Free tier: one till, for a single-counter shop trying the product.</summary>
    public static readonly Guid FreePlanId = new("0193f001-0000-7000-8000-000000000001");

    /// <summary>The default plan a provisioned tenant starts on.</summary>
    public static readonly Guid StarterPlanId = new("0193f001-0000-7000-8000-000000000002");

    public static readonly Guid StandardPlanId = new("0193f001-0000-7000-8000-000000000003");

    public static readonly Guid EnterprisePlanId = new("0193f001-0000-7000-8000-000000000004");

    private readonly RetailFlowDbContext _context;
    private readonly IGuidGenerator _guids;
    private readonly ILogger<SubscriptionPlanSeeder> _logger;

    public SubscriptionPlanSeeder(
        RetailFlowDbContext context,
        IGuidGenerator guids,
        ILogger<SubscriptionPlanSeeder> logger)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(guids);
        ArgumentNullException.ThrowIfNull(logger);

        _context = context;
        _guids = guids;
        _logger = logger;
    }

    public async Task SyncAsync(CancellationToken cancellationToken = default)
    {
        var existing = await _context.SubscriptionPlans
            .Include(p => p.Features)
            .ToDictionaryAsync(p => p.Code, StringComparer.Ordinal, cancellationToken)
            .ConfigureAwait(false);

        var created = 0;
        var featuresAdded = 0;

        foreach (var definition in Catalog)
        {
            if (!existing.TryGetValue(definition.Code, out var plan))
            {
                plan = SubscriptionPlan.Create(
                    definition.Id,
                    definition.Code,
                    definition.Name,
                    definition.MonthlyPrice,
                    trialDays: definition.TrialDays,
                    sortOrder: definition.SortOrder);
                plan.Describe(definition.Description);

                _context.SubscriptionPlans.Add(plan);
                created++;
            }

            foreach (var (code, maxCount, isEnabled) in definition.Features)
            {
                if (plan.Features.Any(f => f.Code == code))
                {
                    continue;
                }

                plan.SetFeature(_guids.NewId(), code, maxCount, isEnabled);
                featuresAdded++;
            }
        }

        if (created > 0 || featuresAdded > 0)
        {
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            PlanSeedLog.PlansSeeded(_logger, created, featuresAdded);
        }
    }

    private sealed record PlanDefinition(
        Guid Id,
        string Code,
        string Name,
        string Description,
        decimal MonthlyPrice,
        int TrialDays,
        int SortOrder,
        IReadOnlyList<(PlanFeatureCode Code, int? MaxCount, bool IsEnabled)> Features);

    /// <summary>
    /// The shipped catalog. Prices are in BDT per month and are starting points for whoever sells
    /// the product; the limits are what the guard enforces. <c>null</c> is unlimited.
    /// </summary>
    private static IReadOnlyList<PlanDefinition> Catalog { get; } =
    [
        new(
            FreePlanId,
            "FREE",
            "Free",
            "One counter, one user. For trying RetailFlow in a single shop.",
            0m,
            TrialDays: 0,
            SortOrder: 0,
            [
                (PlanFeatureCode.Branches, 1, true),
                (PlanFeatureCode.Warehouses, 1, true),
                (PlanFeatureCode.PosTerminals, 1, true),
                (PlanFeatureCode.Users, 2, true),
                (PlanFeatureCode.Products, 200, true),
                (PlanFeatureCode.OfflineMode, null, true),
                (PlanFeatureCode.MultiWarehouse, null, false),
                (PlanFeatureCode.Loyalty, null, false),
                (PlanFeatureCode.CreditSales, null, false),
            ]),
        new(
            StarterPlanId,
            "STARTER",
            "Starter",
            "A single shop with a couple of counters, credit sales and offline selling.",
            1_500m,
            TrialDays: 14,
            SortOrder: 1,
            [
                (PlanFeatureCode.Branches, 1, true),
                (PlanFeatureCode.Warehouses, 3, true),
                (PlanFeatureCode.PosTerminals, 3, true),
                (PlanFeatureCode.Users, 8, true),
                (PlanFeatureCode.Products, 5_000, true),
                (PlanFeatureCode.OfflineMode, null, true),
                (PlanFeatureCode.MultiWarehouse, null, true),
                (PlanFeatureCode.Loyalty, null, false),
                (PlanFeatureCode.CreditSales, null, true),
            ]),
        new(
            StandardPlanId,
            "STANDARD",
            "Standard",
            "Several branches, loyalty and the full back office.",
            4_000m,
            TrialDays: 14,
            SortOrder: 2,
            [
                (PlanFeatureCode.Branches, 5, true),
                (PlanFeatureCode.Warehouses, 20, true),
                (PlanFeatureCode.PosTerminals, 15, true),
                (PlanFeatureCode.Users, 40, true),
                (PlanFeatureCode.Products, 50_000, true),
                (PlanFeatureCode.OfflineMode, null, true),
                (PlanFeatureCode.MultiWarehouse, null, true),
                (PlanFeatureCode.Loyalty, null, true),
                (PlanFeatureCode.CreditSales, null, true),
            ]),
        new(
            EnterprisePlanId,
            "ENTERPRISE",
            "Enterprise",
            "Unlimited branches, tills and users for a chain or a wholesaler.",
            12_000m,
            TrialDays: 30,
            SortOrder: 3,
            [
                (PlanFeatureCode.Branches, null, true),
                (PlanFeatureCode.Warehouses, null, true),
                (PlanFeatureCode.PosTerminals, null, true),
                (PlanFeatureCode.Users, null, true),
                (PlanFeatureCode.Products, null, true),
                (PlanFeatureCode.OfflineMode, null, true),
                (PlanFeatureCode.MultiWarehouse, null, true),
                (PlanFeatureCode.Loyalty, null, true),
                (PlanFeatureCode.CreditSales, null, true),
            ]),
    ];
}

internal static partial class PlanSeedLog
{
    [LoggerMessage(
        EventId = 5100,
        Level = LogLevel.Information,
        Message = "Seeded {PlanCount} subscription plan(s) and {FeatureCount} plan feature(s)")]
    public static partial void PlansSeeded(ILogger logger, int planCount, int featureCount);
}
