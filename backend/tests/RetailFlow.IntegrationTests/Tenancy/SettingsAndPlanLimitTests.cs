using System.Globalization;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using RetailFlow.Application.Platform.UseCases;
using RetailFlow.Application.Tenancy.Abstractions;
using RetailFlow.Application.Tenancy.Contracts;
using RetailFlow.Domain.Tenancy;
using RetailFlow.Infrastructure.Tenancy.Persistence;
using RetailFlow.IntegrationTests.Infrastructure;
using Xunit;

namespace RetailFlow.IntegrationTests.Tenancy;

/// <summary>
/// The configuration chain and the plan limits against a real database (FR-TEN-04, FR-TEN-05).
/// These run through the registered use cases and providers, so the caching and invalidation are
/// exercised rather than bypassed.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class SettingsAndPlanLimitTests : IAsyncLifetime
{
    private readonly DatabaseFixture _fixture;
    private TestTenant _tenant = null!;

    public SettingsAndPlanLimitTests(DatabaseFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await IdentityTestData.EnsurePermissionCatalogAsync(_fixture);
        await TenancyTestData.EnsurePlansAsync(_fixture);
        _tenant = await IdentityTestData.CreateTenantAsync(_fixture, Code("SET"));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string Code(string prefix) =>
        prefix + Random.Shared.Next(1000, 9999).ToString(CultureInfo.InvariantCulture);

    [Fact]
    public async Task Settings_resolve_platform_then_organization_then_branch()
    {
        // Nothing overridden: the platform defaults from appsettings answer.
        await using (var scope = _fixture.CreateScope(_tenant.OrganizationId, _tenant.OwnerUserId, _tenant.BranchId))
        {
            var settings = scope.ServiceProvider.GetRequiredService<IEffectiveSettingsProvider>();
            var resolved = await settings.GetAsync();

            resolved.MaxLineDiscountPercent.Should().Be(10m);
            resolved.ReturnWindowDays.Should().Be(7);
            resolved.PriceIncludesTax.Should().BeTrue();
        }

        // Organization override.
        await using (var scope = _fixture.CreateScope(_tenant.OrganizationId, _tenant.OwnerUserId, _tenant.BranchId))
        {
            var update = scope.ServiceProvider
                .GetRequiredService<IUseCase<UpdateOrganizationSettingsRequest, EffectiveSettingsResponse>>();

            var result = await update.HandleAsync(new UpdateOrganizationSettingsRequest(
                new OrganizationSettingsValues(
                    MaxLineDiscountPercent: 20m,
                    ReturnWindowDays: 14,
                    PriceIncludesTax: false)));

            result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.ToString() : string.Empty);
            result.Value.Effective.MaxLineDiscountPercent.Should().Be(20m);
            result.Value.Effective.ReturnWindowDays.Should().Be(14);
            result.Value.Effective.PriceIncludesTax.Should().BeFalse();
            result.Value.OrganizationOverrides.MaxLineDiscountPercent.Should().Be(20m);
        }

        // Branch override, narrower still.
        await using (var scope = _fixture.CreateScope(_tenant.OrganizationId, _tenant.OwnerUserId, _tenant.BranchId))
        {
            var update = scope.ServiceProvider
                .GetRequiredService<IUseCase<UpdateBranchSettingsRequest, EffectiveSettingsResponse>>();

            var result = await update.HandleAsync(new UpdateBranchSettingsRequest(
                _tenant.BranchId,
                new BranchSettingsValues(MaxLineDiscountPercent: 2.5m)));

            result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.ToString() : string.Empty);
            result.Value.Effective.MaxLineDiscountPercent.Should().Be(2.5m);
            result.Value.Effective.ReturnWindowDays.Should().Be(14, "the branch said nothing about returns");
        }

        // A fresh scope must see the new values, which is what the cache invalidation is for.
        await using (var scope = _fixture.CreateScope(_tenant.OrganizationId, _tenant.OwnerUserId, _tenant.BranchId))
        {
            var settings = scope.ServiceProvider.GetRequiredService<IEffectiveSettingsProvider>();

            var atBranch = await settings.GetAsync();
            atBranch.MaxLineDiscountPercent.Should().Be(2.5m);

            var withoutBranch = await settings
                .GetAsync(_tenant.OrganizationId, branchId: null, terminalId: null);
            withoutBranch.MaxLineDiscountPercent.Should().Be(20m, "no branch in scope means no branch override");
        }
    }

    [Fact]
    public async Task A_terminal_overrides_its_branch_receipt_template_and_warehouse()
    {
        var warehouse = await TenancyTestData
            .AddWarehouseAsync(_fixture, _tenant.OrganizationId, _tenant.BranchId, Code("WH"), isDefault: true);

        var tillWarehouse = await TenancyTestData
            .AddWarehouseAsync(_fixture, _tenant.OrganizationId, _tenant.BranchId, Code("WH"));

        var terminal = await TenancyTestData
            .AddTerminalAsync(_fixture, _tenant.OrganizationId, _tenant.BranchId, Code("T"), tillWarehouse.Id);

        await using var scope = _fixture.CreateScope(_tenant.OrganizationId, _tenant.OwnerUserId, _tenant.BranchId);
        var settings = scope.ServiceProvider.GetRequiredService<IEffectiveSettingsProvider>();

        var withoutTerminal = await settings
            .GetAsync(_tenant.OrganizationId, _tenant.BranchId, terminalId: null);
        withoutTerminal.DefaultWarehouseId.Should().Be(warehouse.Id, "the branch default");

        var withTerminal = await settings
            .GetAsync(_tenant.OrganizationId, _tenant.BranchId, terminal.Id);
        withTerminal.DefaultWarehouseId.Should().Be(tillWarehouse.Id, "the till sells from its own location");
    }

    [Fact]
    public async Task A_discount_cap_above_one_hundred_percent_is_refused_by_validation()
    {
        await using var scope = _fixture.CreateScope(_tenant.OrganizationId, _tenant.OwnerUserId, _tenant.BranchId);
        var update = scope.ServiceProvider
            .GetRequiredService<IUseCase<UpdateOrganizationSettingsRequest, EffectiveSettingsResponse>>();

        var result = await update.HandleAsync(new UpdateOrganizationSettingsRequest(
            new OrganizationSettingsValues(MaxLineDiscountPercent: 150m)));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("request.validation_failed");
        result.Error.FieldErrors.Should().NotBeNull();
        result.Error.FieldErrors!.Keys.Should().Contain(key => key.Contains("MaxLineDiscountPercent"));
    }

    [Fact]
    public async Task The_free_plan_allows_one_branch_and_refuses_the_second()
    {
        await TenancyTestData
            .SubscribeAsync(_fixture, _tenant.OrganizationId, SubscriptionPlanSeeder.FreePlanId);

        // The test tenant already has two branches from the identity fixture, so it is over the
        // free limit before this call; the guard must refuse rather than count only new rows.
        await using var scope = _fixture.CreateScope(_tenant.OrganizationId, _tenant.OwnerUserId, _tenant.BranchId);
        var create = scope.ServiceProvider.GetRequiredService<IUseCase<CreateBranchRequest, BranchResponse>>();

        var result = await create
            .HandleAsync(new CreateBranchRequest(Code("BR"), "Another branch"));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("tenancy.plan_limit_reached");
        result.Error.Message.Should().Contain("branches");
    }

    [Fact]
    public async Task An_unlimited_plan_lets_a_branch_be_created_with_its_default_warehouse()
    {
        await TenancyTestData
            .SubscribeAsync(_fixture, _tenant.OrganizationId, SubscriptionPlanSeeder.EnterprisePlanId);

        await using var scope = _fixture.CreateScope(_tenant.OrganizationId, _tenant.OwnerUserId, _tenant.BranchId);
        var create = scope.ServiceProvider.GetRequiredService<IUseCase<CreateBranchRequest, BranchResponse>>();

        var code = Code("BR");
        var result = await create.HandleAsync(new CreateBranchRequest(code, "Gulshan branch"));

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.ToString() : string.Empty);
        result.Value.Code.Should().Be(code.ToUpperInvariant());
        result.Value.DefaultWarehouseId.Should().NotBeNull("a branch that cannot hold stock is not usable");
    }

    [Fact]
    public async Task A_plan_without_multi_warehouse_refuses_a_second_warehouse_in_a_branch()
    {
        await TenancyTestData
            .SubscribeAsync(_fixture, _tenant.OrganizationId, SubscriptionPlanSeeder.FreePlanId);

        await TenancyTestData
            .AddWarehouseAsync(_fixture, _tenant.OrganizationId, _tenant.BranchId, Code("WH"), isDefault: true);

        await using var scope = _fixture.CreateScope(_tenant.OrganizationId, _tenant.OwnerUserId, _tenant.BranchId);
        var create = scope.ServiceProvider.GetRequiredService<IUseCase<CreateWarehouseRequest, WarehouseResponse>>();

        var result = await create
            .HandleAsync(new CreateWarehouseRequest(_tenant.BranchId, Code("WH"), "Second store"));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("tenancy.feature_not_in_plan");
    }

    [Fact]
    public async Task Moving_to_a_plan_that_current_usage_already_breaches_is_refused()
    {
        await TenancyTestData
            .SubscribeAsync(_fixture, _tenant.OrganizationId, SubscriptionPlanSeeder.EnterprisePlanId);

        await using var scope = _fixture.CreateScope(_tenant.OrganizationId, _tenant.OwnerUserId, _tenant.BranchId);
        var changePlan = scope.ServiceProvider.GetRequiredService<IUseCase<ChangePlanRequest, SubscriptionResponse>>();

        // The fixture tenant has two branches; FREE allows one.
        var result = await changePlan.HandleAsync(new ChangePlanRequest("FREE"));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("tenancy.plan_below_current_usage");
    }

    [Fact]
    public async Task Moving_to_a_plan_that_fits_updates_the_subscription_and_its_usage()
    {
        await TenancyTestData
            .SubscribeAsync(_fixture, _tenant.OrganizationId, SubscriptionPlanSeeder.StarterPlanId);

        await using var scope = _fixture.CreateScope(_tenant.OrganizationId, _tenant.OwnerUserId, _tenant.BranchId);
        var changePlan = scope.ServiceProvider.GetRequiredService<IUseCase<ChangePlanRequest, SubscriptionResponse>>();

        var result = await changePlan.HandleAsync(new ChangePlanRequest("standard"));

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.ToString() : string.Empty);
        result.Value.PlanCode.Should().Be("STANDARD");
        result.Value.Usage.Should().Contain(u => u.Code == PlanFeatureCode.Branches && u.Used == 2);
        result.Value.Usage.Should().Contain(u => u.Code == PlanFeatureCode.Products && !u.IsCounted,
            "products are not countable until the catalog exists, and that is reported rather than enforced");
    }
}
