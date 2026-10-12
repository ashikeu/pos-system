using RetailFlow.Domain.Platform;

namespace RetailFlow.Domain.Tenancy;

/// <summary>
/// The bottom of the configuration chain (FR-TEN-04): the values that apply when neither the
/// organization, the branch nor the terminal has an opinion. Loaded from configuration
/// (<c>Tenancy:Defaults</c>) so a deployment can move the floor without a migration (ADR-0023).
/// </summary>
/// <remarks>
/// Every level above this one stores <c>null</c> to mean "inherit", which is what makes a change
/// here reach tenants that never overrode the value — the alternative, copying defaults into each
/// new organization, freezes them at provisioning time.
/// </remarks>
public sealed record PlatformSettingsDefaults
{
    /// <summary>Whether catalog prices are tax-inclusive (the norm for Bangladeshi retail).</summary>
    public bool PriceIncludesTax { get; init; } = true;

    /// <summary>Whether a sale may drive stock below zero. Off: it hides theft and bad counts.</summary>
    public bool AllowNegativeStock { get; init; }

    /// <summary>Whether a cashier must have an open shift before the POS will sell.</summary>
    public bool RequireOpenShiftToSell { get; init; } = true;

    /// <summary>
    /// Smallest cash increment, in the tenant's currency: 1.00 rounds the cash total to the taka,
    /// which is what a shop with no poisha coins in the drawer needs. Zero disables rounding
    /// (ADR-0006, <see cref="RoundingPolicy.RoundToIncrement"/>).
    /// </summary>
    public decimal CashRoundingIncrement { get; init; } = 1.00m;

    public CashRoundingMode CashRoundingMode { get; init; } = Platform.CashRoundingMode.Nearest;

    /// <summary>Discount percentage a cashier may give before an override permission is needed.</summary>
    public decimal MaxLineDiscountPercent { get; init; } = 10m;

    /// <summary>How long after completion a sale may still be voided rather than returned.</summary>
    public int VoidWindowMinutes { get; init; } = 60;

    public int ReturnWindowDays { get; init; } = 7;

    /// <summary>How long a terminal may keep selling without reaching the server (ADR-0017).</summary>
    public int OfflineMaxDurationHours { get; init; } = 72;

    public decimal OfflineMaxSaleAmount { get; init; } = 50_000m;

    public decimal OfflineMaxCumulativeAmount { get; init; } = 200_000m;

    public bool LoyaltyEnabled { get; init; }

    public bool CreditSalesEnabled { get; init; }

    /// <summary>
    /// Invoice number pattern. Placeholders are expanded by the numbering service in Phase 9:
    /// <c>{BRANCH}</c>, <c>{TERMINAL}</c>, <c>{YYYY}</c>, <c>{MM}</c>, <c>{SEQ}</c>.
    /// </summary>
    public string InvoiceNumberFormat { get; init; } = "{BRANCH}-{YYYY}{MM}-{SEQ}";

    public string ReceiptTemplate58 { get; init; } = "default-58";

    public string ReceiptTemplate80 { get; init; } = "default-80";

    public string? ReceiptFooterText { get; init; }
}

/// <summary>
/// Settings as they apply to one request, after the terminal, branch, organization and platform
/// levels have been merged (FR-TEN-04). Every value is resolved, so a caller never has to know
/// which level answered.
/// </summary>
/// <param name="PriceIncludesTax">Whether catalog prices include tax.</param>
/// <param name="AllowNegativeStock">Whether stock may go below zero.</param>
/// <param name="RequireOpenShiftToSell">Whether an open shift is required to sell.</param>
/// <param name="CashRoundingIncrement">Smallest cash increment.</param>
/// <param name="CashRoundingMode">How the cash total is rounded.</param>
/// <param name="MaxLineDiscountPercent">Discount a cashier may give unaided.</param>
/// <param name="VoidWindowMinutes">Void window after completion.</param>
/// <param name="ReturnWindowDays">Return window in days.</param>
/// <param name="OfflineMaxDurationHours">Maximum offline run time.</param>
/// <param name="OfflineMaxSaleAmount">Maximum single offline sale.</param>
/// <param name="OfflineMaxCumulativeAmount">Maximum unsynced offline total.</param>
/// <param name="LoyaltyEnabled">Whether loyalty accrual is on.</param>
/// <param name="CreditSalesEnabled">Whether selling on customer credit is allowed.</param>
/// <param name="InvoiceNumberFormat">Invoice number pattern.</param>
/// <param name="ReceiptTemplate58">58 mm receipt template.</param>
/// <param name="ReceiptTemplate80">80 mm receipt template.</param>
/// <param name="ReceiptFooterText">Footer line printed on receipts.</param>
/// <param name="DefaultWarehouseId">
/// Warehouse to use for stock movements: the terminal's own, else the branch default. Null when no
/// branch is in scope or the branch has no default warehouse yet.
/// </param>
public sealed record EffectiveSettings(
    bool PriceIncludesTax,
    bool AllowNegativeStock,
    bool RequireOpenShiftToSell,
    decimal CashRoundingIncrement,
    CashRoundingMode CashRoundingMode,
    decimal MaxLineDiscountPercent,
    int VoidWindowMinutes,
    int ReturnWindowDays,
    int OfflineMaxDurationHours,
    decimal OfflineMaxSaleAmount,
    decimal OfflineMaxCumulativeAmount,
    bool LoyaltyEnabled,
    bool CreditSalesEnabled,
    string InvoiceNumberFormat,
    string ReceiptTemplate58,
    string ReceiptTemplate80,
    string? ReceiptFooterText,
    Guid? DefaultWarehouseId);

/// <summary>Values an organization may set. <c>null</c> means "use the platform default".</summary>
/// <param name="PriceIncludesTax">Whether catalog prices include tax.</param>
/// <param name="AllowNegativeStock">Whether stock may go below zero.</param>
/// <param name="RequireOpenShiftToSell">Whether an open shift is required to sell.</param>
/// <param name="CashRoundingIncrement">Smallest cash increment.</param>
/// <param name="CashRoundingMode">How the cash total is rounded.</param>
/// <param name="MaxLineDiscountPercent">Discount a cashier may give unaided.</param>
/// <param name="VoidWindowMinutes">Void window after completion.</param>
/// <param name="ReturnWindowDays">Return window in days.</param>
/// <param name="OfflineMaxDurationHours">Maximum offline run time.</param>
/// <param name="OfflineMaxSaleAmount">Maximum single offline sale.</param>
/// <param name="OfflineMaxCumulativeAmount">Maximum unsynced offline total.</param>
/// <param name="LoyaltyEnabled">Whether loyalty accrual is on.</param>
/// <param name="CreditSalesEnabled">Whether selling on customer credit is allowed.</param>
/// <param name="InvoiceNumberFormat">Invoice number pattern; organization-wide by design.</param>
/// <param name="ReceiptTemplate58">58 mm receipt template.</param>
/// <param name="ReceiptTemplate80">80 mm receipt template.</param>
/// <param name="ReceiptFooterText">Footer line printed on receipts.</param>
public sealed record OrganizationSettingsValues(
    bool? PriceIncludesTax = null,
    bool? AllowNegativeStock = null,
    bool? RequireOpenShiftToSell = null,
    decimal? CashRoundingIncrement = null,
    CashRoundingMode? CashRoundingMode = null,
    decimal? MaxLineDiscountPercent = null,
    int? VoidWindowMinutes = null,
    int? ReturnWindowDays = null,
    int? OfflineMaxDurationHours = null,
    decimal? OfflineMaxSaleAmount = null,
    decimal? OfflineMaxCumulativeAmount = null,
    bool? LoyaltyEnabled = null,
    bool? CreditSalesEnabled = null,
    string? InvoiceNumberFormat = null,
    string? ReceiptTemplate58 = null,
    string? ReceiptTemplate80 = null,
    string? ReceiptFooterText = null);

/// <summary>
/// Values a branch may override. The subset is deliberate: a branch sets what differs about trading
/// at that location, while numbering stays organization-wide so documents are comparable.
/// </summary>
/// <param name="PriceIncludesTax">Whether catalog prices include tax.</param>
/// <param name="AllowNegativeStock">Whether stock may go below zero.</param>
/// <param name="RequireOpenShiftToSell">Whether an open shift is required to sell.</param>
/// <param name="CashRoundingIncrement">Smallest cash increment.</param>
/// <param name="CashRoundingMode">How the cash total is rounded.</param>
/// <param name="MaxLineDiscountPercent">Discount a cashier may give unaided.</param>
/// <param name="VoidWindowMinutes">Void window after completion.</param>
/// <param name="ReturnWindowDays">Return window in days.</param>
/// <param name="OfflineMaxDurationHours">Maximum offline run time.</param>
/// <param name="OfflineMaxSaleAmount">Maximum single offline sale.</param>
/// <param name="OfflineMaxCumulativeAmount">Maximum unsynced offline total.</param>
/// <param name="LoyaltyEnabled">Whether loyalty accrual is on.</param>
/// <param name="CreditSalesEnabled">Whether selling on customer credit is allowed.</param>
/// <param name="ReceiptTemplate58">58 mm receipt template.</param>
/// <param name="ReceiptTemplate80">80 mm receipt template.</param>
/// <param name="ReceiptFooterText">Footer line printed on receipts.</param>
public sealed record BranchSettingsValues(
    bool? PriceIncludesTax = null,
    bool? AllowNegativeStock = null,
    bool? RequireOpenShiftToSell = null,
    decimal? CashRoundingIncrement = null,
    CashRoundingMode? CashRoundingMode = null,
    decimal? MaxLineDiscountPercent = null,
    int? VoidWindowMinutes = null,
    int? ReturnWindowDays = null,
    int? OfflineMaxDurationHours = null,
    decimal? OfflineMaxSaleAmount = null,
    decimal? OfflineMaxCumulativeAmount = null,
    bool? LoyaltyEnabled = null,
    bool? CreditSalesEnabled = null,
    string? ReceiptTemplate58 = null,
    string? ReceiptTemplate80 = null,
    string? ReceiptFooterText = null);

/// <summary>
/// Validation shared by every settings level. A bad value is rejected where it is written rather
/// than where it is read: a 300% discount cap or a negative rounding increment would otherwise
/// surface as a pricing bug at a till.
/// </summary>
internal static class SettingsGuards
{
    public static decimal? Percent(decimal? value, string name)
    {
        if (value is { } percent && (percent < 0m || percent > 100m))
        {
            throw new DomainException($"{name} must be between 0 and 100.");
        }

        return value;
    }

    public static decimal? NonNegativeAmount(decimal? value, string name)
    {
        if (value is not { } amount)
        {
            return null;
        }

        if (amount < 0m)
        {
            throw new DomainException($"{name} must not be negative.");
        }

        return RoundingPolicy.Round4(amount);
    }

    public static int? NonNegative(int? value, string name)
    {
        if (value is { } number && number < 0)
        {
            throw new DomainException($"{name} must not be negative.");
        }

        return value;
    }

    public static int? Positive(int? value, string name)
    {
        if (value is { } number && number <= 0)
        {
            throw new DomainException($"{name} must be greater than zero.");
        }

        return value;
    }

    public static string? Text(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
    }
}

/// <summary>
/// The organization's configuration row: one per tenant, created at provisioning. Stored values are
/// nullable so an untouched setting keeps following the platform default (FR-TEN-04).
/// </summary>
public sealed class OrganizationSettings : TenantEntity
{
    private OrganizationSettings()
    {
    }

    private OrganizationSettings(Guid id, Guid organizationId)
        : base(id, organizationId)
    {
    }

    public bool? PriceIncludesTax { get; private set; }

    public bool? AllowNegativeStock { get; private set; }

    public bool? RequireOpenShiftToSell { get; private set; }

    public decimal? CashRoundingIncrement { get; private set; }

    public CashRoundingMode? CashRoundingMode { get; private set; }

    public decimal? MaxLineDiscountPercent { get; private set; }

    public int? VoidWindowMinutes { get; private set; }

    public int? ReturnWindowDays { get; private set; }

    public int? OfflineMaxDurationHours { get; private set; }

    public decimal? OfflineMaxSaleAmount { get; private set; }

    public decimal? OfflineMaxCumulativeAmount { get; private set; }

    public bool? LoyaltyEnabled { get; private set; }

    public bool? CreditSalesEnabled { get; private set; }

    public string? InvoiceNumberFormat { get; private set; }

    public string? ReceiptTemplate58 { get; private set; }

    public string? ReceiptTemplate80 { get; private set; }

    public string? ReceiptFooterText { get; private set; }

    public static OrganizationSettings CreateDefault(Guid id, Guid organizationId) =>
        new(id, organizationId);

    /// <summary>
    /// Replaces every stored override with the given values. Replace rather than merge, so a client
    /// that wants a setting back on the default sends <c>null</c> and gets exactly that — a merge
    /// would make clearing an override impossible to express.
    /// </summary>
    public void Apply(OrganizationSettingsValues values)
    {
        ArgumentNullException.ThrowIfNull(values);

        PriceIncludesTax = values.PriceIncludesTax;
        AllowNegativeStock = values.AllowNegativeStock;
        RequireOpenShiftToSell = values.RequireOpenShiftToSell;
        CashRoundingIncrement = SettingsGuards.NonNegativeAmount(
            values.CashRoundingIncrement, nameof(CashRoundingIncrement));
        CashRoundingMode = values.CashRoundingMode;
        MaxLineDiscountPercent = SettingsGuards.Percent(
            values.MaxLineDiscountPercent, nameof(MaxLineDiscountPercent));
        VoidWindowMinutes = SettingsGuards.NonNegative(values.VoidWindowMinutes, nameof(VoidWindowMinutes));
        ReturnWindowDays = SettingsGuards.NonNegative(values.ReturnWindowDays, nameof(ReturnWindowDays));
        OfflineMaxDurationHours = SettingsGuards.Positive(
            values.OfflineMaxDurationHours, nameof(OfflineMaxDurationHours));
        OfflineMaxSaleAmount = SettingsGuards.NonNegativeAmount(
            values.OfflineMaxSaleAmount, nameof(OfflineMaxSaleAmount));
        OfflineMaxCumulativeAmount = SettingsGuards.NonNegativeAmount(
            values.OfflineMaxCumulativeAmount, nameof(OfflineMaxCumulativeAmount));
        LoyaltyEnabled = values.LoyaltyEnabled;
        CreditSalesEnabled = values.CreditSalesEnabled;
        InvoiceNumberFormat = SettingsGuards.Text(values.InvoiceNumberFormat, 64);
        ReceiptTemplate58 = SettingsGuards.Text(values.ReceiptTemplate58, 64);
        ReceiptTemplate80 = SettingsGuards.Text(values.ReceiptTemplate80, 64);
        ReceiptFooterText = SettingsGuards.Text(values.ReceiptFooterText, 500);
    }

    /// <summary>The values currently stored, for an edit screen that shows overrides as overrides.</summary>
    public OrganizationSettingsValues ToValues() =>
        new(
            PriceIncludesTax,
            AllowNegativeStock,
            RequireOpenShiftToSell,
            CashRoundingIncrement,
            CashRoundingMode,
            MaxLineDiscountPercent,
            VoidWindowMinutes,
            ReturnWindowDays,
            OfflineMaxDurationHours,
            OfflineMaxSaleAmount,
            OfflineMaxCumulativeAmount,
            LoyaltyEnabled,
            CreditSalesEnabled,
            InvoiceNumberFormat,
            ReceiptTemplate58,
            ReceiptTemplate80,
            ReceiptFooterText);
}

/// <summary>A branch's overrides. One row per branch, created with the branch.</summary>
public sealed class BranchSettings : TenantEntity
{
    private BranchSettings()
    {
    }

    private BranchSettings(Guid id, Guid organizationId, Guid branchId)
        : base(id, organizationId)
    {
        if (branchId == Guid.Empty)
        {
            throw new ArgumentException("Branch id must not be empty.", nameof(branchId));
        }

        BranchId = branchId;
    }

    public Guid BranchId { get; private set; }

    public bool? PriceIncludesTax { get; private set; }

    public bool? AllowNegativeStock { get; private set; }

    public bool? RequireOpenShiftToSell { get; private set; }

    public decimal? CashRoundingIncrement { get; private set; }

    public CashRoundingMode? CashRoundingMode { get; private set; }

    public decimal? MaxLineDiscountPercent { get; private set; }

    public int? VoidWindowMinutes { get; private set; }

    public int? ReturnWindowDays { get; private set; }

    public int? OfflineMaxDurationHours { get; private set; }

    public decimal? OfflineMaxSaleAmount { get; private set; }

    public decimal? OfflineMaxCumulativeAmount { get; private set; }

    public bool? LoyaltyEnabled { get; private set; }

    public bool? CreditSalesEnabled { get; private set; }

    public string? ReceiptTemplate58 { get; private set; }

    public string? ReceiptTemplate80 { get; private set; }

    public string? ReceiptFooterText { get; private set; }

    public static BranchSettings CreateDefault(Guid id, Guid organizationId, Guid branchId) =>
        new(id, organizationId, branchId);

    /// <summary>Replaces every stored override; see <see cref="OrganizationSettings.Apply"/>.</summary>
    public void Apply(BranchSettingsValues values)
    {
        ArgumentNullException.ThrowIfNull(values);

        PriceIncludesTax = values.PriceIncludesTax;
        AllowNegativeStock = values.AllowNegativeStock;
        RequireOpenShiftToSell = values.RequireOpenShiftToSell;
        CashRoundingIncrement = SettingsGuards.NonNegativeAmount(
            values.CashRoundingIncrement, nameof(CashRoundingIncrement));
        CashRoundingMode = values.CashRoundingMode;
        MaxLineDiscountPercent = SettingsGuards.Percent(
            values.MaxLineDiscountPercent, nameof(MaxLineDiscountPercent));
        VoidWindowMinutes = SettingsGuards.NonNegative(values.VoidWindowMinutes, nameof(VoidWindowMinutes));
        ReturnWindowDays = SettingsGuards.NonNegative(values.ReturnWindowDays, nameof(ReturnWindowDays));
        OfflineMaxDurationHours = SettingsGuards.Positive(
            values.OfflineMaxDurationHours, nameof(OfflineMaxDurationHours));
        OfflineMaxSaleAmount = SettingsGuards.NonNegativeAmount(
            values.OfflineMaxSaleAmount, nameof(OfflineMaxSaleAmount));
        OfflineMaxCumulativeAmount = SettingsGuards.NonNegativeAmount(
            values.OfflineMaxCumulativeAmount, nameof(OfflineMaxCumulativeAmount));
        LoyaltyEnabled = values.LoyaltyEnabled;
        CreditSalesEnabled = values.CreditSalesEnabled;
        ReceiptTemplate58 = SettingsGuards.Text(values.ReceiptTemplate58, 64);
        ReceiptTemplate80 = SettingsGuards.Text(values.ReceiptTemplate80, 64);
        ReceiptFooterText = SettingsGuards.Text(values.ReceiptFooterText, 500);
    }

    public BranchSettingsValues ToValues() =>
        new(
            PriceIncludesTax,
            AllowNegativeStock,
            RequireOpenShiftToSell,
            CashRoundingIncrement,
            CashRoundingMode,
            MaxLineDiscountPercent,
            VoidWindowMinutes,
            ReturnWindowDays,
            OfflineMaxDurationHours,
            OfflineMaxSaleAmount,
            OfflineMaxCumulativeAmount,
            LoyaltyEnabled,
            CreditSalesEnabled,
            ReceiptTemplate58,
            ReceiptTemplate80,
            ReceiptFooterText);
}

/// <summary>
/// Merges the four configuration levels into one answer (FR-TEN-04). Pure and synchronous on
/// purpose: the precedence rule is the thing most likely to be got wrong, so it is a function of
/// its inputs that a unit test can pin down exactly.
/// </summary>
public static class SettingsResolution
{
    /// <summary>
    /// Resolves settings for a scope. The narrower level wins: terminal over branch, branch over
    /// organization, organization over the platform default.
    /// </summary>
    /// <param name="defaults">Platform defaults; the only level with no gaps.</param>
    /// <param name="organization">The tenant's row, or null before provisioning completed.</param>
    /// <param name="branch">The branch's row, or null when no branch is in scope.</param>
    /// <param name="terminal">The terminal, or null when the caller is not a till.</param>
    /// <param name="branchDefaultWarehouseId">The branch's default warehouse, when it has one.</param>
    public static EffectiveSettings Resolve(
        PlatformSettingsDefaults defaults,
        OrganizationSettings? organization,
        BranchSettings? branch,
        PosTerminal? terminal,
        Guid? branchDefaultWarehouseId = null)
    {
        ArgumentNullException.ThrowIfNull(defaults);

        return new EffectiveSettings(
            PriceIncludesTax:
                branch?.PriceIncludesTax ?? organization?.PriceIncludesTax ?? defaults.PriceIncludesTax,
            AllowNegativeStock:
                branch?.AllowNegativeStock ?? organization?.AllowNegativeStock ?? defaults.AllowNegativeStock,
            RequireOpenShiftToSell:
                branch?.RequireOpenShiftToSell ?? organization?.RequireOpenShiftToSell
                    ?? defaults.RequireOpenShiftToSell,
            CashRoundingIncrement:
                branch?.CashRoundingIncrement ?? organization?.CashRoundingIncrement
                    ?? defaults.CashRoundingIncrement,
            CashRoundingMode:
                branch?.CashRoundingMode ?? organization?.CashRoundingMode ?? defaults.CashRoundingMode,
            MaxLineDiscountPercent:
                branch?.MaxLineDiscountPercent ?? organization?.MaxLineDiscountPercent
                    ?? defaults.MaxLineDiscountPercent,
            VoidWindowMinutes:
                branch?.VoidWindowMinutes ?? organization?.VoidWindowMinutes ?? defaults.VoidWindowMinutes,
            ReturnWindowDays:
                branch?.ReturnWindowDays ?? organization?.ReturnWindowDays ?? defaults.ReturnWindowDays,
            OfflineMaxDurationHours:
                branch?.OfflineMaxDurationHours ?? organization?.OfflineMaxDurationHours
                    ?? defaults.OfflineMaxDurationHours,
            OfflineMaxSaleAmount:
                branch?.OfflineMaxSaleAmount ?? organization?.OfflineMaxSaleAmount
                    ?? defaults.OfflineMaxSaleAmount,
            OfflineMaxCumulativeAmount:
                branch?.OfflineMaxCumulativeAmount ?? organization?.OfflineMaxCumulativeAmount
                    ?? defaults.OfflineMaxCumulativeAmount,
            LoyaltyEnabled:
                branch?.LoyaltyEnabled ?? organization?.LoyaltyEnabled ?? defaults.LoyaltyEnabled,
            CreditSalesEnabled:
                branch?.CreditSalesEnabled ?? organization?.CreditSalesEnabled ?? defaults.CreditSalesEnabled,

            // Numbering is organization-wide: a branch that invented its own format would make
            // invoice numbers incomparable across a multi-branch tenant (docs/02 §10).
            InvoiceNumberFormat:
                organization?.InvoiceNumberFormat ?? defaults.InvoiceNumberFormat,
            ReceiptTemplate58:
                terminal?.ReceiptTemplate58 ?? branch?.ReceiptTemplate58 ?? organization?.ReceiptTemplate58
                    ?? defaults.ReceiptTemplate58,
            ReceiptTemplate80:
                terminal?.ReceiptTemplate80 ?? branch?.ReceiptTemplate80 ?? organization?.ReceiptTemplate80
                    ?? defaults.ReceiptTemplate80,
            // Configuration providers turn a JSON null into an empty string, and an empty footer
            // would print a blank line on every receipt, so blank is treated as "not set".
            ReceiptFooterText: FirstNonBlank(
                branch?.ReceiptFooterText,
                organization?.ReceiptFooterText,
                defaults.ReceiptFooterText),
            DefaultWarehouseId:
                terminal?.DefaultWarehouseId ?? branchDefaultWarehouseId);
    }

    private static string? FirstNonBlank(params string?[] candidates) =>
        candidates.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
