using RetailFlow.Application.Platform.UseCases;
using RetailFlow.Domain.Pricing;

namespace RetailFlow.Application.Pricing.Contracts;

// ----------------------------------------------------------------- tax classes

/// <param name="IncludeInactive">Whether withdrawn classes are listed too.</param>
public sealed record ListTaxClassesRequest(bool IncludeInactive = false);

/// <param name="TaxClassId">Identifier.</param>
/// <param name="Code">Short code.</param>
/// <param name="Name">Name.</param>
/// <param name="Treatment">Exclusive, inclusive, exempt or zero-rated.</param>
/// <param name="ExemptionReason">Why tax is zero, when it is.</param>
/// <param name="IsDefault">Whether products with no class use it.</param>
/// <param name="IsActive">Whether it may be assigned.</param>
/// <param name="Rules">Its rules and versions.</param>
public sealed record TaxClassResponse(
    Guid TaxClassId,
    string Code,
    string Name,
    TaxTreatment Treatment,
    string? ExemptionReason,
    bool IsDefault,
    bool IsActive,
    IReadOnlyList<TaxRuleResponse> Rules);

/// <param name="TaxRuleId">Identifier.</param>
/// <param name="Name">Name.</param>
/// <param name="BranchId">Branch it is limited to, when it is.</param>
/// <param name="Versions">Its versions, newest first.</param>
public sealed record TaxRuleResponse(
    Guid TaxRuleId,
    string Name,
    Guid? BranchId,
    IReadOnlyList<TaxRuleVersionResponse> Versions);

/// <param name="TaxRuleVersionId">Identifier.</param>
/// <param name="Version">Sequence number within the rule.</param>
/// <param name="VatRate">VAT rate as a fraction.</param>
/// <param name="SdRate">Supplementary duty rate as a fraction.</param>
/// <param name="SdInVatBase">Whether duty sits in the VAT base.</param>
/// <param name="PriceIncludesTax">Whether prices under this version are tax-inclusive.</param>
/// <param name="EffectiveFromUtc">When it took effect.</param>
/// <param name="EffectiveToUtc">When it stopped, or null while current.</param>
/// <param name="SroReference">The SRO or circular it comes from.</param>
/// <param name="Notes">Free-text notes.</param>
public sealed record TaxRuleVersionResponse(
    Guid TaxRuleVersionId,
    int Version,
    decimal VatRate,
    decimal SdRate,
    bool SdInVatBase,
    bool PriceIncludesTax,
    DateTimeOffset EffectiveFromUtc,
    DateTimeOffset? EffectiveToUtc,
    string? SroReference,
    string? Notes);

/// <param name="Code">Short code, unique per tenant.</param>
/// <param name="Name">Name.</param>
/// <param name="Treatment">How prices in this class relate to tax.</param>
/// <param name="ExemptionReason">Required for an exempt or zero-rated class.</param>
/// <param name="MakeDefault">Whether products with no class should use it.</param>
public sealed record CreateTaxClassRequest(
    string Code,
    string Name,
    TaxTreatment Treatment = TaxTreatment.Exclusive,
    string? ExemptionReason = null,
    bool MakeDefault = false) : ITransactionalRequest;

/// <param name="TaxClassId">Class to change.</param>
/// <param name="Name">Name.</param>
/// <param name="Treatment">How prices in this class relate to tax.</param>
/// <param name="ExemptionReason">Required for an exempt or zero-rated class.</param>
/// <param name="MakeDefault">Whether products with no class should use it.</param>
public sealed record UpdateTaxClassRequest(
    Guid TaxClassId,
    string Name,
    TaxTreatment Treatment,
    string? ExemptionReason = null,
    bool MakeDefault = false) : ITransactionalRequest;

/// <summary>
/// Opens a new rate version for a tax class. Rates are never edited in place: a version is added
/// from a date, and the previous one is closed the instant before (ADR-0019).
/// </summary>
/// <param name="TaxClassId">The class the rates apply to.</param>
/// <param name="VatRate">VAT rate as a fraction — 15% is 0.15.</param>
/// <param name="SdRate">Supplementary duty rate as a fraction.</param>
/// <param name="EffectiveFromUtc">When the new rates take effect. Must be after the current version.</param>
/// <param name="BranchId">Limits the rule to one branch.</param>
/// <param name="SdInVatBase">Whether duty forms part of the VAT base (A-02). Defaults to true.</param>
/// <param name="PriceIncludesTax">
/// Whether prices are tax-inclusive under this version. Defaults to the organization's
/// <c>PriceIncludesTax</c> setting, so the two cannot drift apart by accident.
/// </param>
/// <param name="SroReference">The SRO or circular these rates come from.</param>
/// <param name="Notes">Free-text notes for the audit file.</param>
public sealed record AddTaxRateVersionRequest(
    Guid TaxClassId,
    decimal VatRate,
    decimal SdRate,
    DateTimeOffset EffectiveFromUtc,
    Guid? BranchId = null,
    bool SdInVatBase = true,
    bool? PriceIncludesTax = null,
    string? SroReference = null,
    string? Notes = null) : ITransactionalRequest;

/// <summary>
/// Asks what tax would be charged on an amount right now — the "check the rate" screen, and the
/// test a shop runs after changing a rate.
/// </summary>
/// <param name="TaxClassId">Class to use; the tenant default when omitted.</param>
/// <param name="Amount">The unit price to apply tax to.</param>
/// <param name="Quantity">Quantity, so the caller sees a realistic line.</param>
/// <param name="BranchId">Branch, for a branch-specific rule.</param>
/// <param name="AsOfUtc">The instant to resolve at; now when omitted.</param>
public sealed record PreviewTaxRequest(
    Guid? TaxClassId,
    decimal Amount,
    decimal Quantity = 1m,
    Guid? BranchId = null,
    DateTimeOffset? AsOfUtc = null);

/// <param name="Treatment">The treatment that applied.</param>
/// <param name="TaxRuleVersionId">The version used, when there was one.</param>
/// <param name="VatRate">VAT rate used.</param>
/// <param name="SdRate">Duty rate used.</param>
/// <param name="Quantity">Quantity priced.</param>
/// <param name="UnitPrice">Unit price given.</param>
/// <param name="TaxableValue">Value VAT was charged on.</param>
/// <param name="SdAmount">Duty.</param>
/// <param name="VatAmount">VAT.</param>
/// <param name="LineTotal">What the customer would pay.</param>
/// <param name="ExemptionReason">Why tax is zero, when it is.</param>
public sealed record PreviewTaxResponse(
    TaxTreatment Treatment,
    Guid? TaxRuleVersionId,
    decimal VatRate,
    decimal SdRate,
    decimal Quantity,
    decimal UnitPrice,
    decimal TaxableValue,
    decimal SdAmount,
    decimal VatAmount,
    decimal LineTotal,
    string? ExemptionReason);

// ----------------------------------------------------------------- price lists

/// <param name="IncludeInactive">Whether withdrawn lists are included.</param>
public sealed record ListPriceListsRequest(bool IncludeInactive = false);

/// <param name="PriceListId">Identifier.</param>
/// <param name="Code">Short code.</param>
/// <param name="Name">Name.</param>
/// <param name="CustomerGroupId">Group it applies to, when scoped.</param>
/// <param name="BranchId">Branch it applies at, when scoped.</param>
/// <param name="Priority">Higher wins among lists that both apply.</param>
/// <param name="EffectiveFromUtc">Start of its window.</param>
/// <param name="EffectiveToUtc">End of its window.</param>
/// <param name="IsDefault">Whether it is the tenant's base list.</param>
/// <param name="IsActive">Whether it is in use.</param>
/// <param name="ItemCount">How many variants it prices.</param>
public sealed record PriceListResponse(
    Guid PriceListId,
    string Code,
    string Name,
    Guid? CustomerGroupId,
    Guid? BranchId,
    int Priority,
    DateTimeOffset? EffectiveFromUtc,
    DateTimeOffset? EffectiveToUtc,
    bool IsDefault,
    bool IsActive,
    int ItemCount);

/// <param name="Code">Short code, unique per tenant.</param>
/// <param name="Name">Name.</param>
/// <param name="Priority">Higher wins among lists that both apply.</param>
/// <param name="CustomerGroupId">Limits it to a customer group.</param>
/// <param name="BranchId">Limits it to a branch.</param>
/// <param name="EffectiveFromUtc">Start of its window.</param>
/// <param name="EffectiveToUtc">End of its window.</param>
/// <param name="MakeDefault">Whether it becomes the tenant's base list.</param>
public sealed record CreatePriceListRequest(
    string Code,
    string Name,
    int Priority = 0,
    Guid? CustomerGroupId = null,
    Guid? BranchId = null,
    DateTimeOffset? EffectiveFromUtc = null,
    DateTimeOffset? EffectiveToUtc = null,
    bool MakeDefault = false) : ITransactionalRequest;

/// <param name="PriceListId">List to price into.</param>
/// <param name="Items">The prices to set.</param>
public sealed record SetPriceListItemsRequest(
    Guid PriceListId,
    IReadOnlyList<PriceListItemInput> Items) : ITransactionalRequest;

/// <param name="VariantId">The variant to price.</param>
/// <param name="UnitPrice">The price.</param>
/// <param name="MinQuantity">Quantity from which it applies; 0 means any.</param>
public sealed record PriceListItemInput(Guid VariantId, decimal UnitPrice, decimal MinQuantity = 0m);

/// <param name="PriceListId">The list.</param>
/// <param name="ItemsSet">How many prices were written.</param>
public sealed record SetPriceListItemsResponse(Guid PriceListId, int ItemsSet);

/// <param name="BranchId">Branch the price applies at.</param>
/// <param name="VariantId">The variant.</param>
/// <param name="UnitPrice">The price.</param>
/// <param name="EffectiveFromUtc">Start of its window.</param>
/// <param name="EffectiveToUtc">End of its window.</param>
public sealed record SetBranchPriceRequest(
    Guid BranchId,
    Guid VariantId,
    decimal UnitPrice,
    DateTimeOffset? EffectiveFromUtc = null,
    DateTimeOffset? EffectiveToUtc = null) : ITransactionalRequest;

/// <param name="BranchId">The branch.</param>
/// <param name="VariantId">The variant.</param>
/// <param name="UnitPrice">The price.</param>
/// <param name="EffectiveFromUtc">Start of its window.</param>
/// <param name="EffectiveToUtc">End of its window.</param>
/// <param name="IsActive">Whether it is in force.</param>
public sealed record BranchPriceResponse(
    Guid BranchId,
    Guid VariantId,
    decimal UnitPrice,
    DateTimeOffset? EffectiveFromUtc,
    DateTimeOffset? EffectiveToUtc,
    bool IsActive);

/// <param name="BranchId">Branch whose overrides to list.</param>
public sealed record ListBranchPricesRequest(Guid BranchId);

/// <summary>
/// Resolves the price for a variant through the documented order — the screen a shopkeeper uses to
/// ask "why is this ringing up at 95?".
/// </summary>
/// <param name="VariantId">The variant.</param>
/// <param name="Quantity">Quantity, which decides the qualifying tier.</param>
/// <param name="BranchId">Branch; the branch in scope when omitted.</param>
/// <param name="CustomerGroupId">Customer group, for its list.</param>
public sealed record ResolvePriceRequest(
    Guid VariantId,
    decimal Quantity = 1m,
    Guid? BranchId = null,
    Guid? CustomerGroupId = null);

/// <param name="VariantId">The variant priced.</param>
/// <param name="Quantity">Quantity used.</param>
/// <param name="UnitPrice">The resolved price.</param>
/// <param name="Source">Which source won.</param>
/// <param name="SourceId">That source's row.</param>
/// <param name="MinQuantity">The tier that applied.</param>
/// <param name="BasePrice">The variant's base price, for comparison.</param>
public sealed record ResolvePriceResponse(
    Guid VariantId,
    decimal Quantity,
    decimal UnitPrice,
    PriceSource Source,
    Guid? SourceId,
    decimal MinQuantity,
    decimal BasePrice);

// ----------------------------------------------------------------- discounts

/// <param name="IncludeInactive">Whether withdrawn rules are listed too.</param>
public sealed record ListDiscountRulesRequest(bool IncludeInactive = false);

/// <param name="DiscountRuleId">Identifier.</param>
/// <param name="Code">Short code.</param>
/// <param name="Name">Name.</param>
/// <param name="Scope">What it applies to.</param>
/// <param name="Kind">Percentage or fixed amount.</param>
/// <param name="Value">The percentage or the amount.</param>
/// <param name="TargetId">The variant, category or brand it targets.</param>
/// <param name="BranchId">Branch it is limited to.</param>
/// <param name="CustomerGroupId">Customer group it is limited to.</param>
/// <param name="MinQuantity">Smallest qualifying quantity.</param>
/// <param name="MinBasketValue">Smallest qualifying basket.</param>
/// <param name="MaxDiscountAmount">Money cap on a percentage.</param>
/// <param name="EffectiveFromUtc">Start of its window.</param>
/// <param name="EffectiveToUtc">End of its window.</param>
/// <param name="Priority">Higher wins among non-stackable rules.</param>
/// <param name="IsStackable">Whether it combines with another rule.</param>
/// <param name="RequiredPermission">Permission the cashier needs for it to apply.</param>
/// <param name="IsActive">Whether it is in force.</param>
public sealed record DiscountRuleResponse(
    Guid DiscountRuleId,
    string Code,
    string Name,
    DiscountScope Scope,
    DiscountKind Kind,
    decimal Value,
    Guid? TargetId,
    Guid? BranchId,
    Guid? CustomerGroupId,
    decimal? MinQuantity,
    decimal? MinBasketValue,
    decimal? MaxDiscountAmount,
    DateTimeOffset? EffectiveFromUtc,
    DateTimeOffset? EffectiveToUtc,
    int Priority,
    bool IsStackable,
    string? RequiredPermission,
    bool IsActive);

/// <param name="Code">Short code, unique per tenant.</param>
/// <param name="Name">Name.</param>
/// <param name="Scope">What it applies to.</param>
/// <param name="Kind">Percentage or fixed amount.</param>
/// <param name="Value">The percentage (0–100) or the amount.</param>
/// <param name="TargetId">Required for a variant, category or brand scope.</param>
/// <param name="BranchId">Limits it to a branch.</param>
/// <param name="CustomerGroupId">Limits it to a customer group.</param>
/// <param name="MinQuantity">Smallest qualifying quantity.</param>
/// <param name="MinBasketValue">Smallest qualifying basket.</param>
/// <param name="MaxDiscountAmount">Money cap on a percentage.</param>
/// <param name="EffectiveFromUtc">Start of its window.</param>
/// <param name="EffectiveToUtc">End of its window.</param>
/// <param name="Priority">Higher wins among non-stackable rules.</param>
/// <param name="IsStackable">Whether it combines with another rule.</param>
/// <param name="RequiredPermission">Permission the cashier needs for it to apply.</param>
public sealed record CreateDiscountRuleRequest(
    string Code,
    string Name,
    DiscountScope Scope,
    DiscountKind Kind,
    decimal Value,
    Guid? TargetId = null,
    Guid? BranchId = null,
    Guid? CustomerGroupId = null,
    decimal? MinQuantity = null,
    decimal? MinBasketValue = null,
    decimal? MaxDiscountAmount = null,
    DateTimeOffset? EffectiveFromUtc = null,
    DateTimeOffset? EffectiveToUtc = null,
    int Priority = 0,
    bool IsStackable = false,
    string? RequiredPermission = null) : ITransactionalRequest;

/// <param name="DiscountRuleId">Rule to activate or withdraw.</param>
/// <param name="IsActive">Target state.</param>
public sealed record SetDiscountRuleActivationRequest(
    Guid DiscountRuleId,
    bool IsActive) : ITransactionalRequest;
