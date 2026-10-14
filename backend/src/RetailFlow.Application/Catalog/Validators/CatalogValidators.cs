using FluentValidation;
using RetailFlow.Application.Catalog.Contracts;
using RetailFlow.Application.Pricing.Contracts;
using RetailFlow.Domain.Platform;

namespace RetailFlow.Application.Catalog.Validators;

/// <summary>
/// Shared rules for catalog identifiers. Codes, SKUs and barcodes are matched exactly by scanners
/// and import files, so a stray space or slash in one is a lookup that silently misses.
/// </summary>
internal static class CatalogCodeRules
{
    public const string CodePattern = "^[A-Za-z0-9][A-Za-z0-9._/-]{0,63}$";

    public const string CodeMessage =
        "A code may hold letters, digits, dot, dash, underscore and slash, and must start with a letter or digit.";

    public static IRuleBuilderOptions<T, string> CatalogCode<T>(
        this IRuleBuilder<T, string> builder,
        int maxLength = 64) =>
        builder
            .NotEmpty()
            .MaximumLength(maxLength)
            .Matches(CodePattern)
            .WithMessage(CodeMessage);

    /// <summary>A price: never negative, and within what <c>decimal(19,4)</c> can hold.</summary>
    public static IRuleBuilderOptions<T, decimal> Money<T>(this IRuleBuilder<T, decimal> builder) =>
        builder
            .GreaterThanOrEqualTo(0m)
            .LessThanOrEqualTo(999_999_999_999m)
            .WithMessage("An amount must be between 0 and 999,999,999,999.");

    /// <summary>A quantity: positive, and within the platform quantity scale (ADR-0006).</summary>
    public static IRuleBuilderOptions<T, decimal> Quantity<T>(this IRuleBuilder<T, decimal> builder) =>
        builder
            .GreaterThan(0m)
            .LessThanOrEqualTo(999_999_999m)
            .Must(value => RoundingPolicy.RoundQuantity(value) == value)
            .WithMessage($"A quantity may carry at most {RoundingPolicy.QuantityScale} decimal places.");
}

internal sealed class CreateCategoryRequestValidator : AbstractValidator<CreateCategoryRequest>
{
    public CreateCategoryRequestValidator()
    {
        RuleFor(r => r.Code).CatalogCode(32);
        RuleFor(r => r.Name).NotEmpty().MaximumLength(200);
        RuleFor(r => r.NameBn).MaximumLength(200);
    }
}

internal sealed class UpdateCategoryRequestValidator : AbstractValidator<UpdateCategoryRequest>
{
    public UpdateCategoryRequestValidator()
    {
        RuleFor(r => r.CategoryId).NotEmpty();
        RuleFor(r => r.Name).NotEmpty().MaximumLength(200);
        RuleFor(r => r.NameBn).MaximumLength(200);
    }
}

internal sealed class SetCategoryStatusRequestValidator : AbstractValidator<SetCategoryStatusRequest>
{
    public SetCategoryStatusRequestValidator()
    {
        RuleFor(r => r.CategoryId).NotEmpty();
        RuleFor(r => r.Status).IsInEnum();
    }
}

internal sealed class CreateBrandRequestValidator : AbstractValidator<CreateBrandRequest>
{
    public CreateBrandRequestValidator()
    {
        RuleFor(r => r.Code).CatalogCode(32);
        RuleFor(r => r.Name).NotEmpty().MaximumLength(200);
        RuleFor(r => r.NameBn).MaximumLength(200);
    }
}

internal sealed class UpdateBrandRequestValidator : AbstractValidator<UpdateBrandRequest>
{
    public UpdateBrandRequestValidator()
    {
        RuleFor(r => r.BrandId).NotEmpty();
        RuleFor(r => r.Name).NotEmpty().MaximumLength(200);
        RuleFor(r => r.NameBn).MaximumLength(200);
    }
}

internal sealed class CreateUomRequestValidator : AbstractValidator<CreateUomRequest>
{
    public CreateUomRequestValidator()
    {
        RuleFor(r => r.Code).CatalogCode(16);
        RuleFor(r => r.Name).NotEmpty().MaximumLength(100);
        RuleFor(r => r.Kind).IsInEnum();
        RuleFor(r => r.DecimalPlaces).InclusiveBetween(0, RoundingPolicy.QuantityScale);
    }
}

internal sealed class CreateUomConversionRequestValidator : AbstractValidator<CreateUomConversionRequest>
{
    public CreateUomConversionRequestValidator()
    {
        RuleFor(r => r.FromUomId).NotEmpty();
        RuleFor(r => r.ToUomId).NotEmpty().NotEqual(r => r.FromUomId);
        RuleFor(r => r.Factor).GreaterThan(0m).LessThanOrEqualTo(1_000_000m);
    }
}

internal sealed class CreateProductRequestValidator : AbstractValidator<CreateProductRequest>
{
    public CreateProductRequestValidator()
    {
        RuleFor(r => r.Code).CatalogCode();
        RuleFor(r => r.Name).NotEmpty().MaximumLength(300);
        RuleFor(r => r.NameBn).MaximumLength(300);
        RuleFor(r => r.Description).MaximumLength(2000);
        RuleFor(r => r.BasePrice).Money();
        RuleFor(r => r.BaseUomId).NotEmpty().WithMessage("Choose the unit the product's stock is kept in.");
        RuleFor(r => r.ProductType).IsInEnum();
        RuleFor(r => r.BarcodeType).IsInEnum();
        RuleFor(r => r.Sku!)
            .CatalogCode()
            .When(r => !string.IsNullOrWhiteSpace(r.Sku));
        RuleFor(r => r.Barcode).MaximumLength(64);
        RuleFor(r => r.NearExpiryDays).GreaterThanOrEqualTo(0).When(r => r.NearExpiryDays is not null);

        // The same two combinations the aggregate refuses, named here so a form can show them on
        // the right field instead of as a 422 at the bottom of the page.
        RuleFor(r => r.TrackExpiry)
            .Must((request, trackExpiry) => !trackExpiry || request.TrackBatch)
            .WithMessage("Expiry tracking needs batch tracking: an expiry date belongs to a batch.");

        RuleFor(r => r.TrackSerial)
            .Must((request, trackSerial) => !(trackSerial && request.TrackBatch))
            .WithMessage("Track either serials or batches, not both.");
    }
}

internal sealed class UpdateProductRequestValidator : AbstractValidator<UpdateProductRequest>
{
    public UpdateProductRequestValidator()
    {
        RuleFor(r => r.ProductId).NotEmpty();
        RuleFor(r => r.Name).NotEmpty().MaximumLength(300);
        RuleFor(r => r.NameBn).MaximumLength(300);
        RuleFor(r => r.Description).MaximumLength(2000);
        RuleFor(r => r.NearExpiryDays).GreaterThanOrEqualTo(0).When(r => r.NearExpiryDays is not null);

        RuleFor(r => r.TrackExpiry)
            .Must((request, trackExpiry) => !trackExpiry || request.TrackBatch)
            .WithMessage("Expiry tracking needs batch tracking: an expiry date belongs to a batch.");

        RuleFor(r => r.TrackSerial)
            .Must((request, trackSerial) => !(trackSerial && request.TrackBatch))
            .WithMessage("Track either serials or batches, not both.");
    }
}

internal sealed class AddVariantRequestValidator : AbstractValidator<AddVariantRequest>
{
    public AddVariantRequestValidator()
    {
        RuleFor(r => r.ProductId).NotEmpty();
        RuleFor(r => r.Sku).CatalogCode();
        RuleFor(r => r.BasePrice).Money();
        RuleFor(r => r.Name).MaximumLength(200);
        RuleFor(r => r.PackSize).Quantity();
        RuleFor(r => r.WeightInGrams).GreaterThanOrEqualTo(0m).When(r => r.WeightInGrams is not null);
    }
}

internal sealed class UpdateVariantRequestValidator : AbstractValidator<UpdateVariantRequest>
{
    public UpdateVariantRequestValidator()
    {
        RuleFor(r => r.VariantId).NotEmpty();
        RuleFor(r => r.BasePrice).Money();
        RuleFor(r => r.Name).MaximumLength(200);
        RuleFor(r => r.PackSize).Quantity();
        RuleFor(r => r.WeightInGrams).GreaterThanOrEqualTo(0m).When(r => r.WeightInGrams is not null);
    }
}

internal sealed class AddBarcodeRequestValidator : AbstractValidator<AddBarcodeRequest>
{
    public AddBarcodeRequestValidator()
    {
        RuleFor(r => r.VariantId).NotEmpty();
        RuleFor(r => r.Code).NotEmpty().MaximumLength(64);
        RuleFor(r => r.Type).IsInEnum();
        RuleFor(r => r.PackQuantity).Quantity();
    }
}

internal sealed class SetProductAttributeRequestValidator : AbstractValidator<SetProductAttributeRequest>
{
    public SetProductAttributeRequestValidator()
    {
        RuleFor(r => r.ProductId).NotEmpty();
        RuleFor(r => r.Name).NotEmpty().MaximumLength(64);
        RuleFor(r => r.Value).NotEmpty().MaximumLength(256);
    }
}

internal sealed class SetBranchStockPolicyRequestValidator : AbstractValidator<SetBranchStockPolicyRequest>
{
    public SetBranchStockPolicyRequestValidator()
    {
        RuleFor(r => r.VariantId).NotEmpty();
        RuleFor(r => r.BranchId).NotEmpty();
        RuleFor(r => r.MinStock).GreaterThanOrEqualTo(0m).When(r => r.MinStock is not null);
        RuleFor(r => r.ReorderLevel).GreaterThanOrEqualTo(0m).When(r => r.ReorderLevel is not null);
        RuleFor(r => r.ReorderQuantity).GreaterThanOrEqualTo(0m).When(r => r.ReorderQuantity is not null);

        RuleFor(r => r.ReorderLevel)
            .Must((request, level) => request.MinStock is not { } min || level is not { } value || value >= min)
            .WithMessage("The reorder level must be at or above the minimum stock.");
    }
}

internal sealed class LookupProductRequestValidator : AbstractValidator<LookupProductRequest>
{
    public LookupProductRequestValidator()
    {
        RuleFor(r => r.Code).NotEmpty().MaximumLength(64);
        RuleFor(r => r.Quantity).GreaterThan(0m);
    }
}

internal sealed class ImportProductsRequestValidator : AbstractValidator<ImportProductsRequest>
{
    public ImportProductsRequestValidator()
    {
        RuleFor(r => r.Csv)
            .NotEmpty()

            // Roughly 5 000 rows of catalog data. Larger files are split, so a request cannot pin
            // a worker thread parsing megabytes inside one transaction.
            .MaximumLength(4_000_000)
            .WithMessage("That file is too large to import in one request. Split it.");

        RuleFor(r => r.DefaultUomCode).MaximumLength(16);
    }
}

// ----------------------------------------------------------------- pricing

internal sealed class CreateTaxClassRequestValidator : AbstractValidator<CreateTaxClassRequest>
{
    public CreateTaxClassRequestValidator()
    {
        RuleFor(r => r.Code).CatalogCode(32);
        RuleFor(r => r.Name).NotEmpty().MaximumLength(100);
        RuleFor(r => r.Treatment).IsInEnum();
        RuleFor(r => r.ExemptionReason).MaximumLength(256);

        RuleFor(r => r.ExemptionReason)
            .NotEmpty()
            .When(r => r.Treatment is Domain.Pricing.TaxTreatment.Exempt
                or Domain.Pricing.TaxTreatment.ZeroRated)
            .WithMessage("An exempt or zero-rated class needs a reason: the VAT return distinguishes them.");
    }
}

internal sealed class UpdateTaxClassRequestValidator : AbstractValidator<UpdateTaxClassRequest>
{
    public UpdateTaxClassRequestValidator()
    {
        RuleFor(r => r.TaxClassId).NotEmpty();
        RuleFor(r => r.Name).NotEmpty().MaximumLength(100);
        RuleFor(r => r.Treatment).IsInEnum();
        RuleFor(r => r.ExemptionReason).MaximumLength(256);

        RuleFor(r => r.ExemptionReason)
            .NotEmpty()
            .When(r => r.Treatment is Domain.Pricing.TaxTreatment.Exempt
                or Domain.Pricing.TaxTreatment.ZeroRated)
            .WithMessage("An exempt or zero-rated class needs a reason: the VAT return distinguishes them.");
    }
}

internal sealed class AddTaxRateVersionRequestValidator : AbstractValidator<AddTaxRateVersionRequest>
{
    public AddTaxRateVersionRequestValidator()
    {
        RuleFor(r => r.TaxClassId).NotEmpty();

        // The single most likely data-entry error here is 15 instead of 0.15, and it would multiply
        // every bill by sixteen, so the message says so rather than just "out of range".
        RuleFor(r => r.VatRate)
            .InclusiveBetween(0m, 1m)
            .WithMessage("A VAT rate is a fraction: enter 15% as 0.15, not 15.");

        RuleFor(r => r.SdRate)
            .InclusiveBetween(0m, 1m)
            .WithMessage("A supplementary duty rate is a fraction: enter 10% as 0.10, not 10.");

        RuleFor(r => r.EffectiveFromUtc).Must(date => date != default).WithMessage("Give the date the rates start.");
        RuleFor(r => r.SroReference).MaximumLength(64);
        RuleFor(r => r.Notes).MaximumLength(500);
    }
}

internal sealed class PreviewTaxRequestValidator : AbstractValidator<PreviewTaxRequest>
{
    public PreviewTaxRequestValidator()
    {
        RuleFor(r => r.Amount).Money();
        RuleFor(r => r.Quantity).GreaterThan(0m);
    }
}

internal sealed class CreatePriceListRequestValidator : AbstractValidator<CreatePriceListRequest>
{
    public CreatePriceListRequestValidator()
    {
        RuleFor(r => r.Code).CatalogCode(32);
        RuleFor(r => r.Name).NotEmpty().MaximumLength(100);

        RuleFor(r => r.EffectiveToUtc)
            .Must((request, to) => request.EffectiveFromUtc is not { } from || to is not { } value || value > from)
            .WithMessage("A price list cannot end before it starts.");
    }
}

internal sealed class SetPriceListItemsRequestValidator : AbstractValidator<SetPriceListItemsRequest>
{
    public SetPriceListItemsRequestValidator()
    {
        RuleFor(r => r.PriceListId).NotEmpty();
        RuleFor(r => r.Items).NotEmpty();

        RuleForEach(r => r.Items).ChildRules(item =>
        {
            item.RuleFor(i => i.VariantId).NotEmpty();
            item.RuleFor(i => i.UnitPrice).Money();
            item.RuleFor(i => i.MinQuantity).GreaterThanOrEqualTo(0m);
        });
    }
}

internal sealed class SetBranchPriceRequestValidator : AbstractValidator<SetBranchPriceRequest>
{
    public SetBranchPriceRequestValidator()
    {
        RuleFor(r => r.BranchId).NotEmpty();
        RuleFor(r => r.VariantId).NotEmpty();
        RuleFor(r => r.UnitPrice).Money();

        RuleFor(r => r.EffectiveToUtc)
            .Must((request, to) => request.EffectiveFromUtc is not { } from || to is not { } value || value > from)
            .WithMessage("A price override cannot end before it starts.");
    }
}

internal sealed class ResolvePriceRequestValidator : AbstractValidator<ResolvePriceRequest>
{
    public ResolvePriceRequestValidator()
    {
        RuleFor(r => r.VariantId).NotEmpty();
        RuleFor(r => r.Quantity).GreaterThan(0m);
    }
}

internal sealed class CreateDiscountRuleRequestValidator : AbstractValidator<CreateDiscountRuleRequest>
{
    public CreateDiscountRuleRequestValidator()
    {
        RuleFor(r => r.Code).CatalogCode(32);
        RuleFor(r => r.Name).NotEmpty().MaximumLength(100);
        RuleFor(r => r.Scope).IsInEnum();
        RuleFor(r => r.Kind).IsInEnum();
        RuleFor(r => r.Value).GreaterThanOrEqualTo(0m);

        RuleFor(r => r.Value)
            .LessThanOrEqualTo(100m)
            .When(r => r.Kind == Domain.Pricing.DiscountKind.Percent)
            .WithMessage("A percentage discount cannot exceed 100.");

        RuleFor(r => r.TargetId)
            .NotEmpty()
            .When(r => r.Scope is Domain.Pricing.DiscountScope.Variant
                or Domain.Pricing.DiscountScope.Category
                or Domain.Pricing.DiscountScope.Brand)
            .WithMessage("A variant, category or brand discount needs the thing it applies to.");

        RuleFor(r => r.MaxDiscountAmount).GreaterThanOrEqualTo(0m).When(r => r.MaxDiscountAmount is not null);
        RuleFor(r => r.MinBasketValue).GreaterThanOrEqualTo(0m).When(r => r.MinBasketValue is not null);
        RuleFor(r => r.MinQuantity).GreaterThanOrEqualTo(0m).When(r => r.MinQuantity is not null);
        RuleFor(r => r.RequiredPermission).MaximumLength(64);

        RuleFor(r => r.EffectiveToUtc)
            .Must((request, to) => request.EffectiveFromUtc is not { } from || to is not { } value || value > from)
            .WithMessage("A discount rule cannot end before it starts.");
    }
}
