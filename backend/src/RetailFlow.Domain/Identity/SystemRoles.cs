namespace RetailFlow.Domain.Identity;

/// <summary>
/// The role templates every new organization starts with (ADR-0013). They are a starting point a
/// shop can edit, not a fixed hierarchy: a pharmacy and a wholesaler divide the same work
/// differently, so the templates are copied into the tenant rather than shared.
/// </summary>
public static class SystemRoles
{
    public const string Owner = "Owner";
    public const string Manager = "Manager";
    public const string Cashier = "Cashier";
    public const string StockKeeper = "Stock Keeper";
    public const string Accountant = "Accountant";
    public const string Auditor = "Auditor";

    /// <summary>A role template: its name, what it is for, and the permissions it grants.</summary>
    public sealed record Template(string Name, string Description, IReadOnlyList<string> PermissionCodes);

    public static IReadOnlyList<Template> All { get; } = BuildTemplates();

    public static Template For(string name) =>
        All.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"'{name}' is not a system role template.", nameof(name));

    private static List<Template> BuildTemplates()
    {
        // The owner gets everything except the platform-operator permissions, which belong to
        // whoever runs the platform, not to a tenant.
        var ownerPermissions = Permissions.All
            .Where(p => p.Module != Permissions.Platform.Module)
            .Select(p => p.Code)
            .ToArray();

        var cashierPermissions = new[]
        {
            Permissions.Sales.Create,
            Permissions.Sales.View,
            Permissions.Sales.Park,
            Permissions.Sales.Reprint,
            Permissions.Returns.Create,
            Permissions.Returns.View,
            Permissions.Catalog.View,
            Permissions.Pricing.View,
            Permissions.Inventory.View,
            Permissions.Customers.View,
            Permissions.Customers.Create,
            Permissions.Loyalty.View,
            Permissions.Payments.View,
            Permissions.Cash.OpenShift,
            Permissions.Cash.CloseShift,
            Permissions.Cash.ViewShift,
            Permissions.Cash.CashInOut,
            Permissions.Sync.Push,
            Permissions.Sync.Pull,
            Permissions.Sync.ViewStatus,
        };

        // A manager runs branches day to day: approvals, overrides and reports, but not
        // organization-level settings, subscriptions or tax rules.
        var managerExcluded = new HashSet<string>(StringComparer.Ordinal)
        {
            Permissions.Organizations.Update,
            Permissions.Organizations.ManageSettings,
            Permissions.Organizations.ManageSubscription,
            Permissions.Branches.Create,
            Permissions.Branches.Deactivate,
            Permissions.Pricing.ManageTax,
            Permissions.Payments.ManageProviders,
            Permissions.Roles.Delete,
            Permissions.Customers.WriteOffDue,
            Permissions.Users.Create,
            Permissions.Users.Deactivate,
        };

        var managerPermissions = ownerPermissions
            .Where(code => !managerExcluded.Contains(code))
            .ToArray();

        var stockKeeperPermissions = new[]
        {
            Permissions.Catalog.View,
            Permissions.Catalog.Create,
            Permissions.Catalog.Update,
            Permissions.Inventory.View,
            Permissions.Inventory.Adjust,
            Permissions.Inventory.Transfer,
            Permissions.Inventory.ReceiveTransfer,
            Permissions.Inventory.Count,
            Permissions.Inventory.WriteOff,
            Permissions.Procurement.ViewSuppliers,
            Permissions.Procurement.ViewOrders,
            Permissions.Procurement.CreateOrder,
            Permissions.Procurement.ReceiveGoods,
            Permissions.Procurement.ReturnToSupplier,
            Permissions.Reports.ViewInventory,
            Permissions.Reports.ViewPurchases,
            Permissions.Sync.Pull,
            Permissions.Sync.ViewStatus,
        };

        var accountantPermissions = new[]
        {
            Permissions.Sales.View,
            Permissions.Returns.View,
            Permissions.Payments.View,
            Permissions.Payments.Reconcile,
            Permissions.Payments.ResolveUnknown,
            Permissions.Customers.View,
            Permissions.Customers.ManageCredit,
            Permissions.Customers.CollectPayment,
            Permissions.Customers.WriteOffDue,
            Permissions.Procurement.ViewSuppliers,
            Permissions.Procurement.ViewOrders,
            Permissions.Procurement.ManageInvoices,
            Permissions.Procurement.PaySupplier,
            Permissions.Inventory.View,
            Permissions.Inventory.ViewCost,
            Permissions.Cash.ViewShift,
            Permissions.Cash.ApproveVariance,
            Permissions.Cash.AdjustClosedShift,
            Permissions.Reports.ViewSales,
            Permissions.Reports.ViewProfit,
            Permissions.Reports.ViewInventory,
            Permissions.Reports.ViewPurchases,
            Permissions.Reports.ViewCustomers,
            Permissions.Reports.ViewCash,
            Permissions.Reports.ViewExceptions,
            Permissions.Reports.Export,
        };

        // Read-only by construction: an auditor who can change anything is not an auditor.
        var auditorPermissions = Permissions.All
            .Where(p => p.Module != Permissions.Platform.Module)
            .Where(p => p.Code.EndsWith(".view", StringComparison.Ordinal)
                || p.Module == Permissions.Reports.Module
                || p.Code == Permissions.Inventory.ViewCost)
            .Select(p => p.Code)
            .Where(code => code != Permissions.Reports.Export)
            .ToArray();

        return
        [
            new(Owner, "Full access to the organization", ownerPermissions),
            new(Manager, "Runs one or more branches, approves exceptions", managerPermissions),
            new(Cashier, "Operates a POS terminal", cashierPermissions),
            new(StockKeeper, "Receives and moves stock", stockKeeperPermissions),
            new(Accountant, "Reconciles money and reports", accountantPermissions),
            new(Auditor, "Read-only review access", auditorPermissions),
        ];
    }
}
