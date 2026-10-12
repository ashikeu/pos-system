using RetailFlow.Application.Platform.UseCases;
using RetailFlow.Application.Tenancy.Abstractions;
using RetailFlow.Domain.Tenancy;

namespace RetailFlow.Application.Tenancy.Contracts;

// --- provisioning ---

/// <summary>
/// Creates a tenant and everything it needs to trade on the first day: settings, a branch with a
/// default warehouse, one terminal, the role templates, an owner account and a subscription.
/// </summary>
/// <remarks>
/// Idempotent by key (ADR-0010). Provisioning is the operation most likely to be retried after a
/// timeout — it is slow, it is done once per customer, and a duplicate would leave two tenants
/// with the same shop in them.
/// </remarks>
/// <param name="IdempotencyKey">Stable key for this provisioning attempt.</param>
/// <param name="Code">Short tenant code, unique platform-wide; used at sign-in.</param>
/// <param name="Name">Trading name.</param>
/// <param name="OwnerEmail">Email address of the first user, who gets the Owner role.</param>
/// <param name="OwnerFullName">That user's display name.</param>
/// <param name="OwnerPassword">Their initial password; they are asked to change it at first sign-in.</param>
/// <param name="LegalName">Registered name for VAT documents.</param>
/// <param name="BinNumber">NBR Business Identification Number.</param>
/// <param name="PhoneNumber">Contact number.</param>
/// <param name="AddressLine">Head-office address.</param>
/// <param name="City">City.</param>
/// <param name="CurrencyCode">ISO 4217 code; BDT when omitted (assumption A-01).</param>
/// <param name="TimeZoneId">IANA time zone; Asia/Dhaka when omitted.</param>
/// <param name="DefaultLanguage"><c>en</c> or <c>bn</c>.</param>
/// <param name="PlanCode">Plan to start on; the configured default when omitted.</param>
/// <param name="BranchName">Name of the first branch; "Main Branch" when omitted.</param>
public sealed record ProvisionOrganizationRequest(
    string IdempotencyKey,
    string Code,
    string Name,
    string OwnerEmail,
    string OwnerFullName,
    string OwnerPassword,
    string? LegalName = null,
    string? BinNumber = null,
    string? PhoneNumber = null,
    string? AddressLine = null,
    string? City = null,
    string? CurrencyCode = null,
    string? TimeZoneId = null,
    string? DefaultLanguage = null,
    string? PlanCode = null,
    string? BranchName = null) : IIdempotentRequest
{
    /// <summary>Provisioning creates a resource, so a replay answers 201 as the first call did.</summary>
    public int SuccessStatusCode => 201;
}

/// <summary>What provisioning created. Every identifier is returned so a caller can go straight on.</summary>
/// <param name="OrganizationId">The new tenant.</param>
/// <param name="Code">Its sign-in code.</param>
/// <param name="BranchId">The first branch.</param>
/// <param name="WarehouseId">That branch's default warehouse.</param>
/// <param name="TerminalId">The first terminal.</param>
/// <param name="OwnerUserId">The owner account.</param>
/// <param name="SubscriptionId">The subscription row.</param>
/// <param name="PlanCode">Plan the tenant starts on.</param>
/// <param name="TrialEndsOn">When the trial ends, if the plan has one.</param>
public sealed record ProvisionOrganizationResponse(
    Guid OrganizationId,
    string Code,
    Guid BranchId,
    Guid WarehouseId,
    Guid TerminalId,
    Guid OwnerUserId,
    Guid SubscriptionId,
    string PlanCode,
    DateOnly? TrialEndsOn);

// --- organization ---

/// <summary>Reads the current tenant's profile.</summary>
public sealed record GetOrganizationRequest;

/// <param name="OrganizationId">The tenant.</param>
/// <param name="Code">Its code.</param>
/// <param name="Name">Trading name.</param>
/// <param name="NameBn">Trading name in Bangla.</param>
/// <param name="LegalName">Registered name.</param>
/// <param name="BinNumber">VAT BIN.</param>
/// <param name="AddressLine">Address.</param>
/// <param name="City">City.</param>
/// <param name="PhoneNumber">Phone number.</param>
/// <param name="ContactEmail">Billing contact.</param>
/// <param name="CurrencyCode">Currency of every stored amount.</param>
/// <param name="TimeZoneId">Default time zone.</param>
/// <param name="DefaultLanguage">Default UI language.</param>
/// <param name="Status">Lifecycle state.</param>
/// <param name="SuspendedAtUtc">When it was suspended, when it is.</param>
/// <param name="SuspendedReason">Why it was suspended.</param>
public sealed record OrganizationResponse(
    Guid OrganizationId,
    string Code,
    string Name,
    string? NameBn,
    string? LegalName,
    string? BinNumber,
    string? AddressLine,
    string? City,
    string? PhoneNumber,
    string? ContactEmail,
    string CurrencyCode,
    string TimeZoneId,
    string DefaultLanguage,
    OrganizationStatus Status,
    DateTimeOffset? SuspendedAtUtc,
    string? SuspendedReason);

/// <param name="Name">Trading name.</param>
/// <param name="NameBn">Trading name in Bangla.</param>
/// <param name="LegalName">Registered name.</param>
/// <param name="BinNumber">VAT BIN.</param>
/// <param name="AddressLine">Address.</param>
/// <param name="City">City.</param>
/// <param name="PhoneNumber">Phone number.</param>
/// <param name="ContactEmail">Billing contact.</param>
/// <param name="TimeZoneId">Default time zone; unchanged when omitted.</param>
/// <param name="DefaultLanguage"><c>en</c> or <c>bn</c>; unchanged when omitted.</param>
public sealed record UpdateOrganizationRequest(
    string Name,
    string? NameBn = null,
    string? LegalName = null,
    string? BinNumber = null,
    string? AddressLine = null,
    string? City = null,
    string? PhoneNumber = null,
    string? ContactEmail = null,
    string? TimeZoneId = null,
    string? DefaultLanguage = null) : ITransactionalRequest;

/// <summary>Blocks a tenant's writes (platform operators only).</summary>
/// <param name="OrganizationId">The tenant to suspend.</param>
/// <param name="Reason">Why, recorded on the organization and in the audit log.</param>
public sealed record SuspendOrganizationRequest(Guid OrganizationId, string Reason) : ITransactionalRequest;

/// <param name="OrganizationId">The tenant to reactivate.</param>
public sealed record ReactivateOrganizationRequest(Guid OrganizationId) : ITransactionalRequest;

// --- settings ---

/// <summary>Reads settings as they apply to the caller's scope (FR-TEN-04).</summary>
/// <param name="BranchId">Branch to resolve for; the branch in scope when omitted.</param>
/// <param name="TerminalId">Terminal to resolve for; the terminal in scope when omitted.</param>
public sealed record GetEffectiveSettingsRequest(Guid? BranchId = null, Guid? TerminalId = null);

/// <param name="Effective">The merged answer every caller should use.</param>
/// <param name="OrganizationOverrides">What the organization has set, for an edit screen.</param>
/// <param name="BranchOverrides">What the branch has set, when a branch was resolved.</param>
public sealed record EffectiveSettingsResponse(
    EffectiveSettings Effective,
    OrganizationSettingsValues OrganizationOverrides,
    BranchSettingsValues? BranchOverrides);

/// <param name="Values">The complete set of organization overrides; nulls clear an override.</param>
public sealed record UpdateOrganizationSettingsRequest(OrganizationSettingsValues Values) : ITransactionalRequest;

/// <param name="BranchId">Branch whose overrides are being set.</param>
/// <param name="Values">The complete set of branch overrides; nulls fall back to the organization.</param>
public sealed record UpdateBranchSettingsRequest(Guid BranchId, BranchSettingsValues Values) : ITransactionalRequest;

// --- branches ---

/// <param name="IncludeInactive">Whether deactivated branches are listed too.</param>
public sealed record ListBranchesRequest(bool IncludeInactive = false);

/// <param name="BranchId">The branch.</param>
public sealed record GetBranchRequest(Guid BranchId);

/// <param name="BranchId">Identifier.</param>
/// <param name="Code">Branch code.</param>
/// <param name="Name">Branch name.</param>
/// <param name="NameBn">Branch name in Bangla.</param>
/// <param name="AddressLine">Address.</param>
/// <param name="City">City.</param>
/// <param name="PhoneNumber">Phone number.</param>
/// <param name="TimeZoneId">Time zone of this location.</param>
/// <param name="BusinessDayStart">Local time the trading day rolls over.</param>
/// <param name="IsActive">Whether the branch is trading.</param>
/// <param name="DefaultWarehouseId">Its default warehouse, when it has one.</param>
public sealed record BranchResponse(
    Guid BranchId,
    string Code,
    string Name,
    string? NameBn,
    string? AddressLine,
    string? City,
    string? PhoneNumber,
    string TimeZoneId,
    TimeOnly BusinessDayStart,
    bool IsActive,
    Guid? DefaultWarehouseId);

/// <param name="Code">Branch code, unique within the tenant.</param>
/// <param name="Name">Branch name.</param>
/// <param name="NameBn">Branch name in Bangla.</param>
/// <param name="AddressLine">Address.</param>
/// <param name="City">City.</param>
/// <param name="PhoneNumber">Phone number.</param>
/// <param name="TimeZoneId">Time zone; the organization's when omitted.</param>
/// <param name="BusinessDayStart">Business-day start; midnight when omitted (ADR-0007).</param>
/// <param name="CreateDefaultWarehouse">
/// Whether to create a default warehouse with the branch. On by default: a branch without one
/// cannot take stock, and forgetting it surfaces much later as a failed receipt.
/// </param>
public sealed record CreateBranchRequest(
    string Code,
    string Name,
    string? NameBn = null,
    string? AddressLine = null,
    string? City = null,
    string? PhoneNumber = null,
    string? TimeZoneId = null,
    TimeOnly? BusinessDayStart = null,
    bool CreateDefaultWarehouse = true) : ITransactionalRequest;

/// <param name="BranchId">Branch to change.</param>
/// <param name="Name">Branch name.</param>
/// <param name="NameBn">Branch name in Bangla.</param>
/// <param name="AddressLine">Address.</param>
/// <param name="City">City.</param>
/// <param name="PhoneNumber">Phone number.</param>
/// <param name="TimeZoneId">Time zone; unchanged when omitted.</param>
/// <param name="BusinessDayStart">Business-day start; unchanged when omitted.</param>
public sealed record UpdateBranchRequest(
    Guid BranchId,
    string Name,
    string? NameBn = null,
    string? AddressLine = null,
    string? City = null,
    string? PhoneNumber = null,
    string? TimeZoneId = null,
    TimeOnly? BusinessDayStart = null) : ITransactionalRequest;

/// <param name="BranchId">Branch to activate or deactivate.</param>
/// <param name="IsActive">Target state.</param>
public sealed record SetBranchActivationRequest(Guid BranchId, bool IsActive) : ITransactionalRequest;

// --- warehouses ---

/// <param name="BranchId">Branch to list for; all branches when omitted.</param>
/// <param name="IncludeInactive">Whether deactivated warehouses are listed too.</param>
public sealed record ListWarehousesRequest(Guid? BranchId = null, bool IncludeInactive = false);

/// <param name="WarehouseId">Identifier.</param>
/// <param name="BranchId">Owning branch.</param>
/// <param name="Code">Warehouse code.</param>
/// <param name="Name">Warehouse name.</param>
/// <param name="Type">What it is used for.</param>
/// <param name="IsDefault">Whether it is the branch default.</param>
/// <param name="IsActive">Whether it is in use.</param>
public sealed record WarehouseResponse(
    Guid WarehouseId,
    Guid BranchId,
    string Code,
    string Name,
    WarehouseType Type,
    bool IsDefault,
    bool IsActive);

/// <param name="BranchId">Branch the warehouse belongs to.</param>
/// <param name="Code">Warehouse code, unique within the tenant.</param>
/// <param name="Name">Warehouse name.</param>
/// <param name="Type">What it is used for.</param>
/// <param name="MakeDefault">Whether it becomes the branch default.</param>
public sealed record CreateWarehouseRequest(
    Guid BranchId,
    string Code,
    string Name,
    WarehouseType Type = WarehouseType.Main,
    bool MakeDefault = false) : ITransactionalRequest;

/// <param name="WarehouseId">Warehouse to change.</param>
/// <param name="Name">Warehouse name.</param>
/// <param name="MakeDefault">Whether it becomes the branch default.</param>
public sealed record UpdateWarehouseRequest(
    Guid WarehouseId,
    string Name,
    bool MakeDefault = false) : ITransactionalRequest;

/// <param name="WarehouseId">Warehouse to activate or deactivate.</param>
/// <param name="IsActive">Target state.</param>
public sealed record SetWarehouseActivationRequest(Guid WarehouseId, bool IsActive) : ITransactionalRequest;

// --- terminals ---

/// <param name="BranchId">Branch to list for; all branches when omitted.</param>
/// <param name="IncludeInactive">Whether deactivated terminals are listed too.</param>
public sealed record ListTerminalsRequest(Guid? BranchId = null, bool IncludeInactive = false);

/// <param name="TerminalId">Identifier.</param>
/// <param name="BranchId">Owning branch.</param>
/// <param name="Code">Terminal code.</param>
/// <param name="Name">Terminal name.</param>
/// <param name="DefaultWarehouseId">Warehouse this till sells from, when it overrides the branch.</param>
/// <param name="ReceiptTemplate58">58 mm template override.</param>
/// <param name="ReceiptTemplate80">80 mm template override.</param>
/// <param name="IsActive">Whether the till may sell.</param>
/// <param name="LastSyncAtUtc">When it last synced.</param>
/// <param name="AppVersion">Client version last seen.</param>
public sealed record TerminalResponse(
    Guid TerminalId,
    Guid BranchId,
    string Code,
    string Name,
    Guid? DefaultWarehouseId,
    string? ReceiptTemplate58,
    string? ReceiptTemplate80,
    bool IsActive,
    DateTimeOffset? LastSyncAtUtc,
    string? AppVersion);

/// <param name="BranchId">Branch the terminal belongs to.</param>
/// <param name="Code">Terminal code, unique within the tenant.</param>
/// <param name="Name">Terminal name.</param>
/// <param name="DefaultWarehouseId">Warehouse to sell from; the branch default when omitted.</param>
/// <param name="ReceiptTemplate58">58 mm template override.</param>
/// <param name="ReceiptTemplate80">80 mm template override.</param>
public sealed record CreateTerminalRequest(
    Guid BranchId,
    string Code,
    string Name,
    Guid? DefaultWarehouseId = null,
    string? ReceiptTemplate58 = null,
    string? ReceiptTemplate80 = null) : ITransactionalRequest;

/// <param name="TerminalId">Terminal to change.</param>
/// <param name="Name">Terminal name.</param>
/// <param name="DefaultWarehouseId">Warehouse to sell from; the branch default when omitted.</param>
/// <param name="ReceiptTemplate58">58 mm template override.</param>
/// <param name="ReceiptTemplate80">80 mm template override.</param>
public sealed record UpdateTerminalRequest(
    Guid TerminalId,
    string Name,
    Guid? DefaultWarehouseId = null,
    string? ReceiptTemplate58 = null,
    string? ReceiptTemplate80 = null) : ITransactionalRequest;

/// <param name="TerminalId">Terminal to activate or deactivate.</param>
/// <param name="IsActive">Target state.</param>
public sealed record SetTerminalActivationRequest(Guid TerminalId, bool IsActive) : ITransactionalRequest;

/// <summary>
/// Binds a physical device to a terminal and returns the device secret once (ADR-0012). Idempotent,
/// so a till that retries after a timeout does not end up with two registrations.
/// </summary>
/// <param name="IdempotencyKey">Stable key for this registration attempt.</param>
/// <param name="TerminalId">The terminal being registered.</param>
/// <param name="DeviceName">Name to show in the device list.</param>
/// <param name="AppVersion">Client version, recorded on the terminal.</param>
/// <param name="DeviceFingerprint">Machine fingerprint; advisory, never authentication.</param>
public sealed record RegisterTerminalRequest(
    string IdempotencyKey,
    Guid TerminalId,
    string DeviceName,
    string? AppVersion = null,
    string? DeviceFingerprint = null) : IIdempotentRequest
{
    public int SuccessStatusCode => 201;
}

/// <param name="TerminalId">The registered terminal.</param>
/// <param name="BranchId">Its branch.</param>
/// <param name="DeviceId">The device registration.</param>
/// <param name="DeviceSecret">The secret, returned once and never again.</param>
public sealed record RegisterTerminalResponse(
    Guid TerminalId,
    Guid BranchId,
    Guid DeviceId,
    string DeviceSecret);

// --- subscription ---

/// <summary>Lists the plans on offer.</summary>
/// <param name="IncludeWithdrawn">Whether plans no longer sold are listed too.</param>
public sealed record ListPlansRequest(bool IncludeWithdrawn = false);

/// <param name="PlanId">Identifier.</param>
/// <param name="Code">Plan code.</param>
/// <param name="Name">Plan name.</param>
/// <param name="Description">What it includes.</param>
/// <param name="MonthlyPrice">Recurring price per month.</param>
/// <param name="CurrencyCode">Currency of the price.</param>
/// <param name="TrialDays">Free trial length.</param>
/// <param name="IsActive">Whether it may be chosen.</param>
/// <param name="Features">Its limits and switches.</param>
public sealed record PlanResponse(
    Guid PlanId,
    string Code,
    string Name,
    string? Description,
    decimal MonthlyPrice,
    string CurrencyCode,
    int TrialDays,
    bool IsActive,
    IReadOnlyList<PlanFeatureResponse> Features);

/// <param name="Code">The limited feature.</param>
/// <param name="MaxCount">Maximum allowed, or null for unlimited.</param>
/// <param name="IsEnabled">Whether the feature is included.</param>
public sealed record PlanFeatureResponse(PlanFeatureCode Code, int? MaxCount, bool IsEnabled);

/// <summary>Reads the current tenant's subscription and its usage against the plan.</summary>
public sealed record GetSubscriptionRequest;

/// <param name="SubscriptionId">Identifier.</param>
/// <param name="PlanId">Plan in force.</param>
/// <param name="PlanCode">Its code.</param>
/// <param name="PlanName">Its name.</param>
/// <param name="Status">Subscription state.</param>
/// <param name="StartsOn">First day of the subscription.</param>
/// <param name="CurrentPeriodEndsOn">Last day the current period covers.</param>
/// <param name="TrialEndsOn">When the trial ends, if any.</param>
/// <param name="WriteableUntil">Last day writes are accepted, grace included.</param>
/// <param name="AllowsWrites">Whether writes are accepted today.</param>
/// <param name="Usage">Usage against each of the plan's limits.</param>
public sealed record SubscriptionResponse(
    Guid SubscriptionId,
    Guid PlanId,
    string PlanCode,
    string PlanName,
    SubscriptionStatus Status,
    DateOnly StartsOn,
    DateOnly CurrentPeriodEndsOn,
    DateOnly? TrialEndsOn,
    DateOnly WriteableUntil,
    bool AllowsWrites,
    IReadOnlyList<PlanLimitUsage> Usage);

/// <summary>
/// Moves the tenant to another plan. Refused when current usage exceeds the target plan's limits:
/// a downgrade that leaves a shop with more branches than it may have would make every later write
/// fail, which is worse than refusing the downgrade.
/// </summary>
/// <param name="PlanCode">Plan to move to.</param>
public sealed record ChangePlanRequest(string PlanCode) : ITransactionalRequest;

/// <summary>Extends a tenant's subscription period (platform operators only).</summary>
/// <param name="OrganizationId">The tenant.</param>
/// <param name="PeriodEndsOn">New end of the paid period.</param>
public sealed record RenewSubscriptionRequest(Guid OrganizationId, DateOnly PeriodEndsOn) : ITransactionalRequest;
