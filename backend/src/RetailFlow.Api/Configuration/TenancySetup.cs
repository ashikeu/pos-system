using RetailFlow.Application;
using RetailFlow.Application.Tenancy.Contracts;
using RetailFlow.Application.Tenancy.UseCases;

namespace RetailFlow.Api.Configuration;

/// <summary>
/// Registers the tenancy use cases with the standard pipeline. Listed one by one, like the identity
/// ones, so the decorators around each is visible in source (ADR-0015).
/// </summary>
public static class TenancySetup
{
    public static IServiceCollection AddTenancyUseCases(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddUseCase<ProvisionOrganizationRequest, ProvisionOrganizationResponse,
            ProvisionOrganizationHandler>();

        services.AddUseCase<GetOrganizationRequest, OrganizationResponse, GetOrganizationHandler>();
        services.AddUseCase<UpdateOrganizationRequest, OrganizationResponse, UpdateOrganizationHandler>();
        services.AddUseCase<SuspendOrganizationRequest, bool, SuspendOrganizationHandler>();
        services.AddUseCase<ReactivateOrganizationRequest, bool, ReactivateOrganizationHandler>();

        services.AddUseCase<GetEffectiveSettingsRequest, EffectiveSettingsResponse, GetEffectiveSettingsHandler>();
        services.AddUseCase<UpdateOrganizationSettingsRequest, EffectiveSettingsResponse,
            UpdateOrganizationSettingsHandler>();
        services.AddUseCase<UpdateBranchSettingsRequest, EffectiveSettingsResponse, UpdateBranchSettingsHandler>();

        services.AddUseCase<ListBranchesRequest, IReadOnlyList<BranchResponse>, ListBranchesHandler>();
        services.AddUseCase<GetBranchRequest, BranchResponse, GetBranchHandler>();
        services.AddUseCase<CreateBranchRequest, BranchResponse, CreateBranchHandler>();
        services.AddUseCase<UpdateBranchRequest, BranchResponse, UpdateBranchHandler>();
        services.AddUseCase<SetBranchActivationRequest, BranchResponse, SetBranchActivationHandler>();

        services.AddUseCase<ListWarehousesRequest, IReadOnlyList<WarehouseResponse>, ListWarehousesHandler>();
        services.AddUseCase<CreateWarehouseRequest, WarehouseResponse, CreateWarehouseHandler>();
        services.AddUseCase<UpdateWarehouseRequest, WarehouseResponse, UpdateWarehouseHandler>();
        services.AddUseCase<SetWarehouseActivationRequest, WarehouseResponse, SetWarehouseActivationHandler>();

        services.AddUseCase<ListTerminalsRequest, IReadOnlyList<TerminalResponse>, ListTerminalsHandler>();
        services.AddUseCase<CreateTerminalRequest, TerminalResponse, CreateTerminalHandler>();
        services.AddUseCase<UpdateTerminalRequest, TerminalResponse, UpdateTerminalHandler>();
        services.AddUseCase<SetTerminalActivationRequest, TerminalResponse, SetTerminalActivationHandler>();
        services.AddUseCase<RegisterTerminalRequest, RegisterTerminalResponse, RegisterTerminalHandler>();

        services.AddUseCase<ListPlansRequest, IReadOnlyList<PlanResponse>, ListPlansHandler>();
        services.AddUseCase<GetSubscriptionRequest, SubscriptionResponse, GetSubscriptionHandler>();
        services.AddUseCase<ChangePlanRequest, SubscriptionResponse, ChangePlanHandler>();
        services.AddUseCase<RenewSubscriptionRequest, bool, RenewSubscriptionHandler>();

        return services;
    }
}
