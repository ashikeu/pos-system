namespace RetailFlow.Domain.Identity;

/// <summary>
/// The permission catalog (ADR-0013). Codes live in source because they are part of the
/// application's contract, not user data: a role may be edited by a shop owner, but the set of
/// things the software can do changes only when the software does. The codes are seeded into the
/// database so roles can reference them.
/// </summary>
/// <remarks>
/// Codes are <c>module.action</c>, lower snake case, and are append-only: renaming one would
/// silently strip a permission from every role that references it.
/// </remarks>
public static class Permissions
{
    public static class Platform
    {
        public const string Module = "platform";

        /// <summary>Cross-tenant access for platform operators. Every use is audited.</summary>
        public const string ManageTenants = "platform.tenants.manage";

        public const string ViewAuditLog = "platform.audit.view";
        public const string ManageOutbox = "platform.outbox.manage";
    }

    public static class Organizations
    {
        public const string Module = "organizations";

        public const string View = "organizations.view";
        public const string Update = "organizations.update";
        public const string ManageSettings = "organizations.settings.manage";
        public const string ManageSubscription = "organizations.subscription.manage";
    }

    public static class Branches
    {
        public const string Module = "branches";

        public const string View = "branches.view";
        public const string Create = "branches.create";
        public const string Update = "branches.update";
        public const string Deactivate = "branches.deactivate";
        public const string ManageWarehouses = "branches.warehouses.manage";
        public const string ManageTerminals = "branches.terminals.manage";
    }

    public static class Users
    {
        public const string Module = "users";

        public const string View = "users.view";
        public const string Create = "users.create";
        public const string Update = "users.update";
        public const string Deactivate = "users.deactivate";
        public const string ResetPassword = "users.password.reset";
        public const string ManageRoles = "users.roles.manage";
        public const string ManageBranchAccess = "users.branches.manage";
        public const string ViewSessions = "users.sessions.view";
        public const string RevokeSessions = "users.sessions.revoke";
        public const string ManageDevices = "users.devices.manage";
        public const string ViewLoginHistory = "users.login_history.view";
    }

    public static class Roles
    {
        public const string Module = "roles";

        public const string View = "roles.view";
        public const string Create = "roles.create";
        public const string Update = "roles.update";
        public const string Delete = "roles.delete";
    }

    public static class Catalog
    {
        public const string Module = "catalog";

        public const string View = "catalog.view";
        public const string Create = "catalog.create";
        public const string Update = "catalog.update";
        public const string Archive = "catalog.archive";
        public const string Import = "catalog.import";
        public const string Export = "catalog.export";
        public const string ManageCategories = "catalog.categories.manage";
        public const string ManageUnits = "catalog.units.manage";
    }

    public static class Pricing
    {
        public const string Module = "pricing";

        public const string View = "pricing.view";
        public const string Manage = "pricing.manage";
        public const string ManageTax = "pricing.tax.manage";
        public const string ManageDiscounts = "pricing.discounts.manage";
        public const string ManagePromotions = "pricing.promotions.manage";
    }

    public static class Inventory
    {
        public const string Module = "inventory";

        public const string View = "inventory.view";
        public const string ViewCost = "inventory.cost.view";
        public const string Adjust = "inventory.adjust";
        public const string ApproveAdjustment = "inventory.adjust.approve";
        public const string Transfer = "inventory.transfer";
        public const string ReceiveTransfer = "inventory.transfer.receive";
        public const string Count = "inventory.count";
        public const string ApproveCount = "inventory.count.approve";
        public const string WriteOff = "inventory.write_off";
        public const string SellNearExpiry = "inventory.sell_near_expiry";
    }

    public static class Procurement
    {
        public const string Module = "procurement";

        public const string ViewSuppliers = "procurement.suppliers.view";
        public const string ManageSuppliers = "procurement.suppliers.manage";
        public const string ViewOrders = "procurement.orders.view";
        public const string CreateOrder = "procurement.orders.create";
        public const string ApproveOrder = "procurement.orders.approve";
        public const string ReceiveGoods = "procurement.goods.receive";
        public const string ManageInvoices = "procurement.invoices.manage";
        public const string PaySupplier = "procurement.payments.create";
        public const string ReturnToSupplier = "procurement.returns.create";
    }

    public static class Customers
    {
        public const string Module = "customers";

        public const string View = "customers.view";
        public const string Create = "customers.create";
        public const string Update = "customers.update";
        public const string ManageCredit = "customers.credit.manage";
        public const string CollectPayment = "customers.payments.collect";
        public const string WriteOffDue = "customers.due.write_off";
    }

    public static class Loyalty
    {
        public const string Module = "loyalty";

        public const string View = "loyalty.view";
        public const string ManageProgram = "loyalty.program.manage";
        public const string Adjust = "loyalty.adjust";
    }

    public static class Sales
    {
        public const string Module = "sales";

        public const string View = "sales.view";
        public const string Create = "sales.create";
        public const string Park = "sales.park";
        public const string RecallOtherTerminal = "sales.park.recall_other";
        public const string Void = "sales.void";
        public const string Reprint = "sales.reprint";
        public const string OverridePrice = "sales.price.override";
        public const string DiscountBeyondLimit = "sales.discount.override";
        public const string FullDiscount = "sales.discount.full";
        public const string SellBelowCost = "sales.price.below_cost";
        public const string CreditSale = "sales.credit.create";
        public const string OverrideCreditLimit = "sales.credit.override";
    }

    public static class Returns
    {
        public const string Module = "returns";

        public const string View = "returns.view";
        public const string Create = "returns.create";
        public const string CreateBlind = "returns.create_blind";
        public const string Approve = "returns.approve";
        public const string RefundCash = "returns.refund.cash";
        public const string OverrideRefundLimit = "returns.refund.override";
        public const string OutsideWindow = "returns.outside_window";
    }

    public static class Payments
    {
        public const string Module = "payments";

        public const string View = "payments.view";
        public const string ManageProviders = "payments.providers.manage";
        public const string Reconcile = "payments.reconcile";
        public const string Refund = "payments.refund";
        public const string ResolveUnknown = "payments.unknown.resolve";
    }

    public static class Cash
    {
        public const string Module = "cash";

        public const string OpenShift = "cash.shift.open";
        public const string CloseShift = "cash.shift.close";
        public const string ViewShift = "cash.shift.view";
        public const string CloseOtherCashierShift = "cash.shift.close_other";
        public const string ApproveVariance = "cash.variance.approve";
        public const string CashInOut = "cash.movement.create";
        public const string RecordExpense = "cash.expense.create";
        public const string AdjustClosedShift = "cash.shift.adjust";
    }

    public static class Reports
    {
        public const string Module = "reports";

        public const string ViewSales = "reports.sales.view";
        public const string ViewProfit = "reports.profit.view";
        public const string ViewInventory = "reports.inventory.view";
        public const string ViewPurchases = "reports.purchases.view";
        public const string ViewCustomers = "reports.customers.view";
        public const string ViewCash = "reports.cash.view";
        public const string ViewExceptions = "reports.exceptions.view";
        public const string ViewAudit = "reports.audit.view";
        public const string Export = "reports.export";
    }

    public static class Sync
    {
        public const string Module = "sync";

        public const string Push = "sync.push";
        public const string Pull = "sync.pull";
        public const string ViewStatus = "sync.status.view";
        public const string ResolveConflict = "sync.conflict.resolve";
    }

    /// <summary>A permission code with the module it belongs to and what it allows.</summary>
    public sealed record Definition(string Code, string Module, string Description);

    /// <summary>
    /// Every permission in the system, used to seed the <c>Permission</c> table and to validate
    /// that a role only references codes that exist.
    /// </summary>
    public static IReadOnlyList<Definition> All { get; } = BuildCatalog();

    public static IReadOnlySet<string> AllCodes { get; } =
        All.Select(p => p.Code).ToHashSet(StringComparer.Ordinal);

    public static bool Exists(string code) => AllCodes.Contains(code);

    private static List<Definition> BuildCatalog() =>
    [
        new(Platform.ManageTenants, Platform.Module, "Administer tenants across organizations"),
        new(Platform.ViewAuditLog, Platform.Module, "Read the platform audit log"),
        new(Platform.ManageOutbox, Platform.Module, "Inspect and requeue outbox messages"),

        new(Organizations.View, Organizations.Module, "View organization details"),
        new(Organizations.Update, Organizations.Module, "Change organization details"),
        new(Organizations.ManageSettings, Organizations.Module, "Change organization settings"),
        new(Organizations.ManageSubscription, Organizations.Module, "Manage the subscription and plan"),

        new(Branches.View, Branches.Module, "View branches"),
        new(Branches.Create, Branches.Module, "Create a branch"),
        new(Branches.Update, Branches.Module, "Change branch details"),
        new(Branches.Deactivate, Branches.Module, "Deactivate a branch"),
        new(Branches.ManageWarehouses, Branches.Module, "Manage warehouses"),
        new(Branches.ManageTerminals, Branches.Module, "Manage POS terminals"),

        new(Users.View, Users.Module, "View users"),
        new(Users.Create, Users.Module, "Create a user"),
        new(Users.Update, Users.Module, "Change user details"),
        new(Users.Deactivate, Users.Module, "Deactivate a user"),
        new(Users.ResetPassword, Users.Module, "Reset another user's password"),
        new(Users.ManageRoles, Users.Module, "Assign roles to users"),
        new(Users.ManageBranchAccess, Users.Module, "Grant branch access"),
        new(Users.ViewSessions, Users.Module, "View active sessions"),
        new(Users.RevokeSessions, Users.Module, "Revoke sessions"),
        new(Users.ManageDevices, Users.Module, "Register and revoke terminal devices"),
        new(Users.ViewLoginHistory, Users.Module, "View login history"),

        new(Roles.View, Roles.Module, "View roles"),
        new(Roles.Create, Roles.Module, "Create a role"),
        new(Roles.Update, Roles.Module, "Change a role"),
        new(Roles.Delete, Roles.Module, "Delete a role"),

        new(Catalog.View, Catalog.Module, "View products"),
        new(Catalog.Create, Catalog.Module, "Create products"),
        new(Catalog.Update, Catalog.Module, "Change products"),
        new(Catalog.Archive, Catalog.Module, "Archive products"),
        new(Catalog.Import, Catalog.Module, "Import products from a file"),
        new(Catalog.Export, Catalog.Module, "Export the catalog"),
        new(Catalog.ManageCategories, Catalog.Module, "Manage categories and brands"),
        new(Catalog.ManageUnits, Catalog.Module, "Manage units of measure"),

        new(Pricing.View, Pricing.Module, "View prices"),
        new(Pricing.Manage, Pricing.Module, "Manage price lists"),
        new(Pricing.ManageTax, Pricing.Module, "Manage tax rules"),
        new(Pricing.ManageDiscounts, Pricing.Module, "Manage discount rules"),
        new(Pricing.ManagePromotions, Pricing.Module, "Manage promotions and coupons"),

        new(Inventory.View, Inventory.Module, "View stock balances and movements"),
        new(Inventory.ViewCost, Inventory.Module, "See cost and valuation figures"),
        new(Inventory.Adjust, Inventory.Module, "Raise a stock adjustment"),
        new(Inventory.ApproveAdjustment, Inventory.Module, "Approve a stock adjustment"),
        new(Inventory.Transfer, Inventory.Module, "Dispatch a stock transfer"),
        new(Inventory.ReceiveTransfer, Inventory.Module, "Receive a stock transfer"),
        new(Inventory.Count, Inventory.Module, "Perform a stock count"),
        new(Inventory.ApproveCount, Inventory.Module, "Approve and post a stock count"),
        new(Inventory.WriteOff, Inventory.Module, "Write off damaged or expired stock"),
        new(Inventory.SellNearExpiry, Inventory.Module, "Sell stock close to expiry"),

        new(Procurement.ViewSuppliers, Procurement.Module, "View suppliers"),
        new(Procurement.ManageSuppliers, Procurement.Module, "Manage suppliers"),
        new(Procurement.ViewOrders, Procurement.Module, "View purchase orders"),
        new(Procurement.CreateOrder, Procurement.Module, "Create a purchase order"),
        new(Procurement.ApproveOrder, Procurement.Module, "Approve a purchase order"),
        new(Procurement.ReceiveGoods, Procurement.Module, "Receive goods"),
        new(Procurement.ManageInvoices, Procurement.Module, "Manage purchase invoices"),
        new(Procurement.PaySupplier, Procurement.Module, "Record supplier payments"),
        new(Procurement.ReturnToSupplier, Procurement.Module, "Return goods to a supplier"),

        new(Customers.View, Customers.Module, "View customers"),
        new(Customers.Create, Customers.Module, "Create a customer"),
        new(Customers.Update, Customers.Module, "Change customer details"),
        new(Customers.ManageCredit, Customers.Module, "Set credit limits"),
        new(Customers.CollectPayment, Customers.Module, "Collect a customer payment"),
        new(Customers.WriteOffDue, Customers.Module, "Write off a customer due"),

        new(Loyalty.View, Loyalty.Module, "View loyalty accounts"),
        new(Loyalty.ManageProgram, Loyalty.Module, "Manage the loyalty program"),
        new(Loyalty.Adjust, Loyalty.Module, "Adjust loyalty points"),

        new(Sales.View, Sales.Module, "View sales"),
        new(Sales.Create, Sales.Module, "Ring up a sale"),
        new(Sales.Park, Sales.Module, "Park and recall a bill"),
        new(Sales.RecallOtherTerminal, Sales.Module, "Recall a bill parked on another terminal"),
        new(Sales.Void, Sales.Module, "Void a sale"),
        new(Sales.Reprint, Sales.Module, "Reprint a receipt"),
        new(Sales.OverridePrice, Sales.Module, "Override a line price"),
        new(Sales.DiscountBeyondLimit, Sales.Module, "Give a discount above the configured limit"),
        new(Sales.FullDiscount, Sales.Module, "Give a 100% discount"),
        new(Sales.SellBelowCost, Sales.Module, "Sell below cost"),
        new(Sales.CreditSale, Sales.Module, "Sell on customer credit"),
        new(Sales.OverrideCreditLimit, Sales.Module, "Exceed a customer credit limit"),

        new(Returns.View, Returns.Module, "View returns"),
        new(Returns.Create, Returns.Module, "Process a return against a sale"),
        new(Returns.CreateBlind, Returns.Module, "Process a return without the original sale"),
        new(Returns.Approve, Returns.Module, "Approve a return"),
        new(Returns.RefundCash, Returns.Module, "Refund in cash"),
        new(Returns.OverrideRefundLimit, Returns.Module, "Refund more than the refundable amount"),
        new(Returns.OutsideWindow, Returns.Module, "Accept a return outside the return window"),

        new(Payments.View, Payments.Module, "View payments"),
        new(Payments.ManageProviders, Payments.Module, "Configure payment providers"),
        new(Payments.Reconcile, Payments.Module, "Reconcile provider settlements"),
        new(Payments.Refund, Payments.Module, "Issue a payment refund"),
        new(Payments.ResolveUnknown, Payments.Module, "Resolve payments with an unknown outcome"),

        new(Cash.OpenShift, Cash.Module, "Open a cash session"),
        new(Cash.CloseShift, Cash.Module, "Close own cash session"),
        new(Cash.ViewShift, Cash.Module, "View cash sessions"),
        new(Cash.CloseOtherCashierShift, Cash.Module, "Close another cashier's session"),
        new(Cash.ApproveVariance, Cash.Module, "Approve a cash variance"),
        new(Cash.CashInOut, Cash.Module, "Record cash in and cash out"),
        new(Cash.RecordExpense, Cash.Module, "Record an expense from the till"),
        new(Cash.AdjustClosedShift, Cash.Module, "Adjust a closed shift"),

        new(Reports.ViewSales, Reports.Module, "View sales reports"),
        new(Reports.ViewProfit, Reports.Module, "View profit and margin reports"),
        new(Reports.ViewInventory, Reports.Module, "View inventory reports"),
        new(Reports.ViewPurchases, Reports.Module, "View purchase reports"),
        new(Reports.ViewCustomers, Reports.Module, "View customer reports"),
        new(Reports.ViewCash, Reports.Module, "View cash reconciliation reports"),
        new(Reports.ViewExceptions, Reports.Module, "View void, refund and override reports"),
        new(Reports.ViewAudit, Reports.Module, "View audit history"),
        new(Reports.Export, Reports.Module, "Export reports"),

        new(Sync.Push, Sync.Module, "Push offline operations"),
        new(Sync.Pull, Sync.Module, "Pull reference data"),
        new(Sync.ViewStatus, Sync.Module, "View synchronization status"),
        new(Sync.ResolveConflict, Sync.Module, "Resolve a synchronization conflict"),
    ];
}
