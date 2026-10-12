using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RetailFlow.Application.Identity.Contracts;
using RetailFlow.Application.Platform.UseCases;
using RetailFlow.Application.Tenancy.Contracts;
using RetailFlow.Domain.Identity;
using RetailFlow.Domain.Tenancy;
using RetailFlow.Infrastructure.Platform.Persistence;
using RetailFlow.Infrastructure.Tenancy.Persistence;
using RetailFlow.IntegrationTests.Infrastructure;
using Xunit;

namespace RetailFlow.IntegrationTests.Tenancy;

/// <summary>
/// Organization provisioning end to end (FR-TEN-01, FR-TEN-05). The test that matters most is the
/// last one: the owner account provisioning created can actually sign in and act, which is the only
/// real proof that a tenant was created in a usable state.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ProvisioningTests : IAsyncLifetime
{
    private readonly DatabaseFixture _fixture;
    private ApiFactory? _factory;
    private TestTenant _operatorTenant = null!;

    public ProvisioningTests(DatabaseFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(_fixture.ConnectionString);
        await IdentityTestData.EnsurePermissionCatalogAsync(_fixture);
        await TenancyTestData.EnsurePlansAsync(_fixture);

        _operatorTenant = await IdentityTestData.CreateTenantAsync(_fixture, Code("OPS"));
        await TenancyTestData
            .MakePlatformOperatorAsync(_fixture, _operatorTenant.OrganizationId, _operatorTenant.OwnerUserId);
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }

    private ApiFactory Factory => _factory ?? throw new InvalidOperationException("Not initialised.");

    private static string Code(string prefix) =>
        prefix + Random.Shared.Next(1000, 9999).ToString(CultureInfo.InvariantCulture);

    [Fact]
    public async Task Provisioning_creates_a_tenant_that_can_trade_on_day_one()
    {
        var code = Code("NEW");
        var result = await ProvisionAsync(code);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.ToString() : string.Empty);

        var response = result.Value;
        response.Code.Should().Be(code);
        response.PlanCode.Should().Be("STARTER");
        response.TrialEndsOn.Should().NotBeNull("the starter plan ships with a trial");

        await using var scope = _fixture.CreateScope(response.OrganizationId);
        var context = scope.ServiceProvider.GetRequiredService<RetailFlowDbContext>();

        var organization = await context.Organizations.FindAsync(response.OrganizationId);
        organization.Should().NotBeNull();
        organization!.Status.Should().Be(OrganizationStatus.Active);
        organization.CurrencyCode.Should().Be("BDT");

        (await context.OrganizationSettings.CountAsync())
            .Should().Be(1, "settings are created with the tenant");

        var branch = await context.Branches.FindAsync(response.BranchId);
        branch.Should().NotBeNull();
        branch!.Code.Should().Be("MAIN");

        var warehouse = await context.Warehouses.FindAsync(response.WarehouseId);
        warehouse.Should().NotBeNull();
        warehouse!.IsDefault.Should().BeTrue();
        warehouse.BranchId.Should().Be(response.BranchId);

        var terminal = await context.PosTerminals.FindAsync(response.TerminalId);
        terminal.Should().NotBeNull();
        terminal!.DefaultWarehouseId.Should().Be(response.WarehouseId);

        var subscription = await context.OrganizationSubscriptions
            .FirstOrDefaultAsync(s => s.Id == response.SubscriptionId);
        subscription.Should().NotBeNull();
        subscription!.Status.Should().Be(SubscriptionStatus.Trialing);

        var roles = await context.Roles.CountAsync();
        roles.Should().Be(SystemRoles.All.Count, "every tenant gets its own copy of the templates");

        var owner = await context.Users.FindAsync(response.OwnerUserId);
        owner.Should().NotBeNull();
        owner!.MustChangePassword.Should().BeTrue("the operator chose that password, not the owner");
    }

    [Fact]
    public async Task The_provisioned_owner_can_sign_in_and_see_its_own_organization()
    {
        var code = Code("SIGN");
        var result = await ProvisionAsync(code);
        result.IsSuccess.Should().BeTrue();

        using var client = await TenancyTestData
            .SignedInClientAsync(Factory, code, $"owner@{code.ToLowerInvariant()}.example", "ProvisionPass!2026");

        var organization = await client.GetFromJsonAsync<OrganizationResponse>("/api/v1/organization", TestJson.Options);

        organization.Should().NotBeNull();
        organization!.OrganizationId.Should().Be(result.Value.OrganizationId);
        organization.Code.Should().Be(code);

        var branches = await client.GetFromJsonAsync<List<BranchResponse>>("/api/v1/branches");
        branches.Should().ContainSingle().Which.BranchId.Should().Be(result.Value.BranchId);

        var subscription = await client.GetFromJsonAsync<SubscriptionResponse>("/api/v1/subscription", TestJson.Options);
        subscription!.PlanCode.Should().Be("STARTER");
        subscription.AllowsWrites.Should().BeTrue();
        subscription.Usage.Should().Contain(u => u.Code == PlanFeatureCode.Branches && u.Used == 1);
    }

    [Fact]
    public async Task A_duplicate_organization_code_is_refused()
    {
        var code = Code("DUP");

        (await ProvisionAsync(code)).IsSuccess.Should().BeTrue();

        var second = await ProvisionAsync(code);

        second.IsFailure.Should().BeTrue();
        second.Error.Code.Should().Be("tenancy.organization_code_taken");
    }

    [Fact]
    public async Task Replaying_the_same_idempotency_key_returns_the_first_tenant_rather_than_a_second_one()
    {
        var code = Code("IDEM");
        var key = "provision-" + Guid.NewGuid();

        var first = await ProvisionAsync(code, key);
        first.IsSuccess.Should().BeTrue();

        var replay = await ProvisionAsync(code, key);

        replay.IsSuccess.Should().BeTrue("a retry after a lost response must not fail");
        replay.Value.OrganizationId.Should().Be(first.Value.OrganizationId);
        replay.Value.OwnerUserId.Should().Be(first.Value.OwnerUserId);

        await using var scope = _fixture.CreateScope(first.Value.OrganizationId);
        var context = scope.ServiceProvider.GetRequiredService<RetailFlowDbContext>();
        var matching = await context.Organizations
            .IgnoreQueryFilters()
            .CountAsync(o => o.Code == code);

        matching.Should().Be(1);
    }

    [Fact]
    public async Task A_weak_owner_password_is_refused_before_anything_is_created()
    {
        var code = Code("WEAK");

        var result = await ProvisionAsync(code, password: "password");

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("identity.password_policy");

        await using var scope = _fixture.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RetailFlowDbContext>();
        (await context.Organizations.IgnoreQueryFilters().CountAsync(o => o.Code == code))
            .Should().Be(0);
    }

    [Fact]
    public async Task An_unknown_plan_code_is_refused()
    {
        var result = await ProvisionAsync(Code("PLAN"), planCode: "GOLD");

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("tenancy.plan_not_found");
    }

    [Fact]
    public async Task A_tenant_user_without_the_platform_permission_cannot_provision_over_http()
    {
        var tenant = await IdentityTestData.CreateTenantAsync(_fixture, Code("NOPE"));

        using var client = await TenancyTestData
            .SignedInClientAsync(Factory, tenant.OrganizationCode, tenant.OwnerEmail, tenant.Password);

        var code = Code("HTTP");
        var response = await client.PostAsJsonAsync(
            "/api/v1/platform/organizations",
            new ProvisionOrganizationRequest(
                IdempotencyKey: Guid.NewGuid().ToString(),
                Code: code,
                Name: code + " Shop",
                OwnerEmail: $"owner@{code.ToLowerInvariant()}.example",
                OwnerFullName: "Test Owner",
                OwnerPassword: "ProvisionPass!2026"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "the Owner template deliberately excludes the platform permissions");
    }

    [Fact]
    public async Task A_platform_operator_can_provision_over_http_with_the_key_in_the_header()
    {
        using var client = await TenancyTestData
            .SignedInClientAsync(
                Factory,
                _operatorTenant.OrganizationCode,
                _operatorTenant.OwnerEmail,
                _operatorTenant.Password);

        var code = Code("OPH");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/platform/organizations")
        {
            Content = JsonContent.Create(new ProvisionOrganizationRequest(
                IdempotencyKey: string.Empty,
                Code: code,
                Name: code + " Shop",
                OwnerEmail: $"owner@{code.ToLowerInvariant()}.example",
                OwnerFullName: "Test Owner",
                OwnerPassword: "ProvisionPass!2026",
                PlanCode: "FREE")),
        };
        request.Headers.Add("Idempotency-Key", "header-" + Guid.NewGuid());

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await response.Content.ReadFromJsonAsync<ProvisionOrganizationResponse>(TestJson.Options);
        body!.Code.Should().Be(code);
        body.PlanCode.Should().Be("FREE");
    }

    private async Task<Domain.Platform.Result<ProvisionOrganizationResponse>> ProvisionAsync(
        string code,
        string? idempotencyKey = null,
        string password = "ProvisionPass!2026",
        string? planCode = null)
    {
        await using var scope = _fixture.CreateScope(
            _operatorTenant.OrganizationId,
            _operatorTenant.OwnerUserId);

        var handler = scope.ServiceProvider
            .GetRequiredService<IUseCase<ProvisionOrganizationRequest, ProvisionOrganizationResponse>>();

        return await handler.HandleAsync(new ProvisionOrganizationRequest(
            IdempotencyKey: idempotencyKey ?? "provision-" + Guid.NewGuid(),
            Code: code,
            Name: code + " Shop",
            OwnerEmail: $"owner@{code.ToLowerInvariant()}.example",
            OwnerFullName: "Test Owner",
            OwnerPassword: password,
            LegalName: code + " Limited",
            BinNumber: "123456789",
            PhoneNumber: "+8801700000000",
            AddressLine: "12 Test Road",
            City: "Dhaka",
            PlanCode: planCode));
    }
}
