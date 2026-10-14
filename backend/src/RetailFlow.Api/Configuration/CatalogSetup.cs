using RetailFlow.Application;
using RetailFlow.Application.Catalog.Abstractions;
using RetailFlow.Application.Catalog.Contracts;
using RetailFlow.Application.Catalog.UseCases;
using RetailFlow.Application.Platform.Contracts;
using RetailFlow.Application.Pricing.Contracts;
using RetailFlow.Application.Pricing.UseCases;

namespace RetailFlow.Api.Configuration;

/// <summary>
/// Registers the catalog and pricing use cases with the standard pipeline, one by one, so the
/// decorators around each are visible in source (ADR-0015).
/// </summary>
public static class CatalogSetup
{
    public static IServiceCollection AddCatalogUseCases(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // --- classification ---
        services.AddUseCase<ListCategoriesRequest, IReadOnlyList<CategoryResponse>, ListCategoriesHandler>();
        services.AddUseCase<CreateCategoryRequest, CategoryResponse, CreateCategoryHandler>();
        services.AddUseCase<UpdateCategoryRequest, CategoryResponse, UpdateCategoryHandler>();
        services.AddUseCase<SetCategoryStatusRequest, CategoryResponse, SetCategoryStatusHandler>();
        services.AddUseCase<ListBrandsRequest, IReadOnlyList<BrandResponse>, ListBrandsHandler>();
        services.AddUseCase<CreateBrandRequest, BrandResponse, CreateBrandHandler>();
        services.AddUseCase<UpdateBrandRequest, BrandResponse, UpdateBrandHandler>();
        services.AddUseCase<SetBrandStatusRequest, BrandResponse, SetBrandStatusHandler>();
        services.AddUseCase<ListUomsRequest, IReadOnlyList<UomResponse>, ListUomsHandler>();
        services.AddUseCase<CreateUomRequest, UomResponse, CreateUomHandler>();
        services.AddUseCase<ListUomConversionsRequest, IReadOnlyList<UomConversionResponse>,
            ListUomConversionsHandler>();
        services.AddUseCase<CreateUomConversionRequest, UomConversionResponse, CreateUomConversionHandler>();

        // --- products ---
        services.AddUseCase<SearchProductsRequest, PagedResult<ProductListItem>, SearchProductsHandler>();
        services.AddUseCase<GetProductRequest, ProductResponse, GetProductHandler>();
        services.AddUseCase<CreateProductRequest, ProductResponse, CreateProductHandler>();
        services.AddUseCase<UpdateProductRequest, ProductResponse, UpdateProductHandler>();
        services.AddUseCase<SetProductStatusRequest, ProductResponse, SetProductStatusHandler>();
        services.AddUseCase<AddVariantRequest, ProductResponse, AddVariantHandler>();
        services.AddUseCase<UpdateVariantRequest, ProductResponse, UpdateVariantHandler>();
        services.AddUseCase<SetVariantStatusRequest, ProductResponse, SetVariantStatusHandler>();
        services.AddUseCase<AddBarcodeRequest, ProductResponse, AddBarcodeHandler>();
        services.AddUseCase<SetProductAttributeRequest, ProductResponse, SetProductAttributeHandler>();
        services.AddUseCase<SetBranchStockPolicyRequest, BranchStockPolicyResponse, SetBranchStockPolicyHandler>();
        services.AddUseCase<ListBranchStockPoliciesRequest, IReadOnlyList<BranchStockPolicyResponse>,
            ListBranchStockPoliciesHandler>();

        // --- the POS path ---
        services.AddUseCase<LookupProductRequest, LookupProductResponse, LookupProductHandler>();

        // --- import and export ---
        services.AddUseCase<ImportProductsRequest, ImportProductsResponse, ImportProductsHandler>();
        services.AddUseCase<ExportProductsRequest, ExportProductsResponse, ExportProductsHandler>();

        // --- tax ---
        services.AddUseCase<ListTaxClassesRequest, IReadOnlyList<TaxClassResponse>, ListTaxClassesHandler>();
        services.AddUseCase<CreateTaxClassRequest, TaxClassResponse, CreateTaxClassHandler>();
        services.AddUseCase<UpdateTaxClassRequest, TaxClassResponse, UpdateTaxClassHandler>();
        services.AddUseCase<AddTaxRateVersionRequest, TaxClassResponse, AddTaxRateVersionHandler>();
        services.AddUseCase<PreviewTaxRequest, PreviewTaxResponse, PreviewTaxHandler>();

        // --- pricing ---
        services.AddUseCase<ListPriceListsRequest, IReadOnlyList<PriceListResponse>, ListPriceListsHandler>();
        services.AddUseCase<CreatePriceListRequest, PriceListResponse, CreatePriceListHandler>();
        services.AddUseCase<SetPriceListItemsRequest, SetPriceListItemsResponse, SetPriceListItemsHandler>();
        services.AddUseCase<SetBranchPriceRequest, BranchPriceResponse, SetBranchPriceHandler>();
        services.AddUseCase<ListBranchPricesRequest, IReadOnlyList<BranchPriceResponse>, ListBranchPricesHandler>();
        services.AddUseCase<ResolvePriceRequest, ResolvePriceResponse, ResolvePriceHandler>();
        services.AddUseCase<ListDiscountRulesRequest, IReadOnlyList<DiscountRuleResponse>,
            ListDiscountRulesHandler>();
        services.AddUseCase<CreateDiscountRuleRequest, DiscountRuleResponse, CreateDiscountRuleHandler>();
        services.AddUseCase<SetDiscountRuleActivationRequest, DiscountRuleResponse,
            SetDiscountRuleActivationHandler>();

        return services;
    }
}
