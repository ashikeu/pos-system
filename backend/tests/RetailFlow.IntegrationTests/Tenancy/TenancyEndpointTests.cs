using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using RetailFlow.Application.Identity.Contracts;
using RetailFlow.Application.Tenancy.Contracts;
using RetailFlow.Domain.Tenancy;
using RetailFlow.Infrastructure.Tenancy.Persistence;
using RetailFlow.IntegrationTests.Infrastructure;
using Xunit;

namespace RetailFlow.IntegrationTests.Tenancy;

/// <summary>
/// The tenancy endpoints over real HTTP: authorization, tenant isolation, terminal registration and
/// the read-only gate a suspended or lapsed tenant gets (FR-TEN-02, FR-TEN-03, FR-TEN-06).
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class TenancyEndpointTests : IAsyncLifetime
{
    private readonly DatabaseFixture _fixture;
    private ApiFactory? _factory;
    private TestTenant _tenant = null!;

    public TenancyEndpointTests(DatabaseFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(_fixture.ConnectionString);
        await IdentityTestData.EnsurePermissionCatalogAsync(_fixture).ConfigureAwait(false);
        await TenancyTestData.EnsurePlansAsync(_fixture).ConfigureAwait(false);

        _tenant = await IdentityTestData.CreateTenantAsync(_fixture, Code("TEN")).ConfigureAwait(false);
        await TenancyTestData
            .SubscribeAsync(_fixture, _tenant.OrganizationId, SubscriptionPlanSeeder.EnterprisePlanId)
            .ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync().ConfigureAwait(false);
        }
    }

    private ApiFactory Factory => _factory ?? throw new InvalidOperationException("Not initialised.");

    private static string Code(string prefix) =>
        prefix + Random.Shared.Next(1000, 9999).ToString(CultureInfo.InvariantCulture);

    private Task<HttpClient> OwnerClientAsync() => TenancyTestData.SignedInClientAsync(
        Factory, _tenant.OrganizationCode, _tenant.OwnerEmail, _tenant.Password);

    private Task<HttpClient> CashierClientAsync() => TenancyTestData.SignedInClientAsync(
        Factory, _tenant.OrganizationCode, _tenant.CashierEmail, _tenant.Password);

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    [Fact]
    public async Task An_owner_can_create_a_branch_with_a_warehouse_and_a_terminal()
    {
        using var client = await OwnerClientAsync();

        var branchCode = Code("BR");
        var created = await client.PostAsJsonAsync(
            "/api/v1/branches",
            new CreateBranchRequest(branchCode, "Dhanmondi branch", City: "Dhaka"));

        created.StatusCode.Should().Be(HttpStatusCode.Created);
        created.Headers.Location.Should().NotBeNull();

        var branch = await created.Content.ReadFromJsonAsync<BranchResponse>(TestJson.Options);
        branch!.Code.Should().Be(branchCode.ToUpperInvariant());
        branch.DefaultWarehouseId.Should().NotBeNull();

        var terminalCode = Code("T");
        var terminal = await client.PostAsJsonAsync(
            "/api/v1/terminals",
            new CreateTerminalRequest(branch.BranchId, terminalCode, "Counter 1", branch.DefaultWarehouseId));

        terminal.StatusCode.Should().Be(HttpStatusCode.Created);

        var terminalBody = await terminal.Content.ReadFromJsonAsync<TerminalResponse>(TestJson.Options);
        terminalBody!.DefaultWarehouseId.Should().Be(branch.DefaultWarehouseId);

        var warehouses = await client.GetFromJsonAsync<List<WarehouseResponse>>(
            $"/api/v1/warehouses?branchId={branch.BranchId}", TestJson.Options);
        warehouses.Should().ContainSingle().Which.IsDefault.Should().BeTrue();
    }

    [Fact]
    public async Task A_terminal_cannot_point_at_another_branchs_warehouse()
    {
        using var client = await OwnerClientAsync();

        var otherWarehouse = await TenancyTestData.AddWarehouseAsync(
            _fixture, _tenant.OrganizationId, _tenant.SecondBranchId, Code("WH"));

        var response = await client.PostAsJsonAsync(
            "/api/v1/terminals",
            new CreateTerminalRequest(_tenant.BranchId, Code("T"), "Counter X", otherWarehouse.Id));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCodeAsync(response)).Should().Be("tenancy.warehouse_branch_mismatch");
    }

    [Fact]
    public async Task A_cashier_cannot_create_a_branch_or_read_the_organization()
    {
        using var client = await CashierClientAsync();

        var create = await client.PostAsJsonAsync(
            "/api/v1/branches",
            new CreateBranchRequest(Code("BR"), "Not allowed"));

        create.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var read = await client.GetAsync("/api/v1/organization");
        read.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "the cashier template has no organizations.view permission");
    }

    [Fact]
    public async Task One_tenant_cannot_read_or_change_another_tenants_branch()
    {
        var other = await IdentityTestData.CreateTenantAsync(_fixture, Code("OTH"));

        using var client = await OwnerClientAsync();

        var read = await client.GetAsync($"/api/v1/branches/{other.BranchId}");
        read.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "another tenant's branch simply does not exist from here");

        var update = await client.PutAsJsonAsync(
            $"/api/v1/branches/{other.BranchId}",
            new UpdateBranchRequest(other.BranchId, "Hijacked"));

        update.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Registering_a_terminal_returns_a_device_secret_once_and_replays_it_on_retry()
    {
        using var client = await OwnerClientAsync();

        var terminal = await TenancyTestData.AddTerminalAsync(
            _fixture, _tenant.OrganizationId, _tenant.BranchId, Code("T"));

        var key = "register-" + Guid.NewGuid();

        var first = await SendRegisterAsync(client, terminal.Id, key);
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await first.Content.ReadFromJsonAsync<RegisterTerminalResponse>(TestJson.Options);
        body!.DeviceSecret.Should().NotBeNullOrWhiteSpace();
        body.BranchId.Should().Be(_tenant.BranchId);

        var replay = await SendRegisterAsync(client, terminal.Id, key);
        replay.StatusCode.Should().Be(HttpStatusCode.Created);

        var replayBody = await replay.Content.ReadFromJsonAsync<RegisterTerminalResponse>(TestJson.Options);
        replayBody!.DeviceId.Should().Be(body.DeviceId, "a retry must not mint a second device");
        replayBody.DeviceSecret.Should().Be(body.DeviceSecret);
    }

    [Fact]
    public async Task Registering_without_an_idempotency_key_is_refused()
    {
        using var client = await OwnerClientAsync();

        var terminal = await TenancyTestData.AddTerminalAsync(
            _fixture, _tenant.OrganizationId, _tenant.BranchId, Code("T"));

        var response = await client.PostAsJsonAsync(
            $"/api/v1/terminals/{terminal.Id}/register",
            new RegisterTerminalRequest(string.Empty, terminal.Id, "Till machine"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Settings_can_be_read_and_changed_over_http()
    {
        using var client = await OwnerClientAsync();

        var before = await client.GetFromJsonAsync<EffectiveSettingsResponse>("/api/v1/organization/settings", TestJson.Options);
        before!.Effective.MaxLineDiscountPercent.Should().Be(10m, "the platform default from appsettings");

        var update = await client.PutAsJsonAsync(
            "/api/v1/organization/settings",
            new UpdateOrganizationSettingsRequest(new OrganizationSettingsValues(
                MaxLineDiscountPercent: 15m,
                CreditSalesEnabled: true)));

        update.StatusCode.Should().Be(HttpStatusCode.OK);

        var after = await client.GetFromJsonAsync<EffectiveSettingsResponse>("/api/v1/organization/settings", TestJson.Options);
        after!.Effective.MaxLineDiscountPercent.Should().Be(15m);
        after.Effective.CreditSalesEnabled.Should().BeTrue();
        after.OrganizationOverrides.CreditSalesEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task An_invalid_setting_is_a_four_hundred_naming_the_field()
    {
        using var client = await OwnerClientAsync();

        var response = await client.PutAsJsonAsync(
            "/api/v1/organization/settings",
            new UpdateOrganizationSettingsRequest(new OrganizationSettingsValues(MaxLineDiscountPercent: 150m)));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCodeAsync(response)).Should().Be("request.validation_failed");
    }

    [Fact]
    public async Task A_suspended_tenant_can_sign_in_and_read_but_not_write()
    {
        var suspended = await IdentityTestData.CreateTenantAsync(_fixture, Code("SUS"));
        await TenancyTestData.SubscribeAsync(
            _fixture, suspended.OrganizationId, SubscriptionPlanSeeder.EnterprisePlanId);
        await TenancyTestData.SuspendAsync(_fixture, suspended.OrganizationId, "payment overdue");

        using var client = await TenancyTestData.SignedInClientAsync(
            Factory, suspended.OrganizationCode, suspended.OwnerEmail, suspended.Password);

        var read = await client.GetAsync("/api/v1/branches");
        read.StatusCode.Should().Be(HttpStatusCode.OK, "a suspended shop still needs to see its own data");

        var write = await client.PostAsJsonAsync(
            "/api/v1/branches",
            new CreateBranchRequest(Code("BR"), "Should not happen"));

        write.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(write)).Should().Be("tenancy.organization_read_only");

        // The renewal surface stays open: this is how the shop gets itself out of the state.
        var plans = await client.GetAsync("/api/v1/subscription/plans");
        plans.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_closed_tenant_cannot_sign_in_at_all()
    {
        var closed = await IdentityTestData.CreateTenantAsync(_fixture, Code("CLO"));
        await TenancyTestData.CloseAsync(_fixture, closed.OrganizationId, "shop sold");

        using var client = Factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new LoginRequest(closed.OrganizationCode, closed.OwnerEmail, closed.Password));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(response)).Should().Be("identity.organization_unavailable");
    }

    [Fact]
    public async Task A_lapsed_subscription_makes_the_tenant_read_only_and_renewal_restores_it()
    {
        var lapsed = await IdentityTestData.CreateTenantAsync(_fixture, Code("LAP"));

        // A period that ended a month ago, with no grace left.
        await TenancyTestData.SubscribeAsync(
            _fixture,
            lapsed.OrganizationId,
            SubscriptionPlanSeeder.EnterprisePlanId,
            periodDays: 30,
            trialDays: 0,
            graceDays: 0,
            startsOn: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-60)));

        using var client = await TenancyTestData.SignedInClientAsync(
            Factory, lapsed.OrganizationCode, lapsed.OwnerEmail, lapsed.Password);

        var blocked = await client.PostAsJsonAsync(
            "/api/v1/branches",
            new CreateBranchRequest(Code("BR"), "Should not happen"));

        blocked.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(blocked)).Should().Be("tenancy.organization_read_only");

        var subscription = await client.GetFromJsonAsync<SubscriptionResponse>("/api/v1/subscription", TestJson.Options);
        subscription!.AllowsWrites.Should().BeFalse();

        // An operator renews it; writes come back without anyone signing in again.
        var operatorTenant = await IdentityTestData.CreateTenantAsync(_fixture, Code("OPR"));
        await TenancyTestData.MakePlatformOperatorAsync(
            _fixture, operatorTenant.OrganizationId, operatorTenant.OwnerUserId);
        await TenancyTestData.SubscribeAsync(
            _fixture, operatorTenant.OrganizationId, SubscriptionPlanSeeder.EnterprisePlanId);

        using var operatorClient = await TenancyTestData.SignedInClientAsync(
            Factory, operatorTenant.OrganizationCode, operatorTenant.OwnerEmail, operatorTenant.Password);

        var renew = await operatorClient.PostAsJsonAsync(
            $"/api/v1/platform/organizations/{lapsed.OrganizationId}/subscription/renew",
            new RenewSubscriptionRequest(
                lapsed.OrganizationId,
                DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30))));

        renew.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var afterRenewal = await client.GetFromJsonAsync<SubscriptionResponse>("/api/v1/subscription", TestJson.Options);
        afterRenewal!.AllowsWrites.Should().BeTrue();
        afterRenewal.Status.Should().Be(SubscriptionStatus.Active);
    }

    [Fact]
    public async Task An_operator_can_suspend_and_reactivate_a_tenant()
    {
        var target = await IdentityTestData.CreateTenantAsync(_fixture, Code("TGT"));
        var operatorTenant = await IdentityTestData.CreateTenantAsync(_fixture, Code("OPS"));
        await TenancyTestData.MakePlatformOperatorAsync(
            _fixture, operatorTenant.OrganizationId, operatorTenant.OwnerUserId);

        using var client = await TenancyTestData.SignedInClientAsync(
            Factory, operatorTenant.OrganizationCode, operatorTenant.OwnerEmail, operatorTenant.Password);

        var suspend = await client.PostAsJsonAsync(
            $"/api/v1/platform/organizations/{target.OrganizationId}/suspend",
            new SuspendOrganizationRequest(target.OrganizationId, "trial expired"));

        suspend.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var reactivate = await client.PostAsync(
            $"/api/v1/platform/organizations/{target.OrganizationId}/reactivate",
            content: null);

        reactivate.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task The_last_active_branch_cannot_be_deactivated()
    {
        var single = await IdentityTestData.CreateTenantAsync(_fixture, Code("ONE"));
        await TenancyTestData.SubscribeAsync(
            _fixture, single.OrganizationId, SubscriptionPlanSeeder.EnterprisePlanId);

        using var client = await TenancyTestData.SignedInClientAsync(
            Factory, single.OrganizationCode, single.OwnerEmail, single.Password);

        // The fixture tenant has two branches; deactivate one, then the other must be refused.
        var first = await client.PostAsync($"/api/v1/branches/{single.SecondBranchId}/deactivate", content: null);
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await client.PostAsync($"/api/v1/branches/{single.BranchId}/deactivate", content: null);
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorCodeAsync(second)).Should().Be("tenancy.last_active_branch");
    }

    private static Task<HttpResponseMessage> SendRegisterAsync(HttpClient client, Guid terminalId, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/terminals/{terminalId}/register")
        {
            Content = JsonContent.Create(new RegisterTerminalRequest(
                IdempotencyKey: string.Empty,
                TerminalId: terminalId,
                DeviceName: "Till machine",
                AppVersion: "1.0.0")),
        };
        request.Headers.Add("Idempotency-Key", key);

        return client.SendAsync(request);
    }
}
