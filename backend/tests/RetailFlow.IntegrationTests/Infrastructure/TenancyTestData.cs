using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RetailFlow.Application.Identity.Abstractions;
using RetailFlow.Application.Identity.Contracts;
using RetailFlow.Application.Platform.Abstractions;
using RetailFlow.Application.Tenancy.Abstractions;
using RetailFlow.Domain.Identity;
using RetailFlow.Domain.Tenancy;
using RetailFlow.Infrastructure.Platform.Persistence;
using RetailFlow.Infrastructure.Tenancy.Persistence;

namespace RetailFlow.IntegrationTests.Infrastructure;

/// <summary>
/// Setup for the tenancy tests: plan catalog, subscriptions, platform operators and a sign-in
/// helper. Kept apart from <see cref="IdentityTestData"/> so an identity test is not quietly
/// affected by what a subscription test needs.
/// </summary>
internal static class TenancyTestData
{
    /// <summary>Seeds the shipped plan catalog, as the API does on start-up.</summary>
    public static async Task EnsurePlansAsync(DatabaseFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        await using var scope = fixture.CreateScope();
        await scope.ServiceProvider.GetRequiredService<SubscriptionPlanSeeder>()
            .SyncAsync()
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Gives a tenant a subscription. <paramref name="startsOn"/> and <paramref name="periodDays"/>
    /// are explicit so a test can create a period that has already ended.
    /// </summary>
    public static async Task<OrganizationSubscription> SubscribeAsync(
        DatabaseFixture fixture,
        Guid organizationId,
        Guid planId,
        int periodDays = 30,
        int trialDays = 0,
        int graceDays = 7,
        DateOnly? startsOn = null)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        await using var scope = fixture.CreateScope(organizationId);
        var context = scope.ServiceProvider.GetRequiredService<RetailFlowDbContext>();
        var guids = scope.ServiceProvider.GetRequiredService<IGuidGenerator>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();

        var existing = await context.OrganizationSubscriptions
            .IgnoreQueryFilters()
            .Where(s => s.OrganizationId == organizationId)
            .ToListAsync()
            .ConfigureAwait(false);

        context.OrganizationSubscriptions.RemoveRange(existing);

        var subscription = OrganizationSubscription.Start(
            guids.NewId(),
            organizationId,
            planId,
            startsOn ?? DateOnly.FromDateTime(clock.UtcNow.UtcDateTime),
            periodDays,
            trialDays,
            graceDays);

        context.OrganizationSubscriptions.Add(subscription);
        await context.SaveChangesAsync().ConfigureAwait(false);

        // Clears the fixture provider's own cached answer. The API host has a separate cache, so
        // a test that checks the write gate uses a tenant it has not written to before.
        await scope.ServiceProvider.GetRequiredService<ITenantStatusProvider>()
            .InvalidateAsync(organizationId)
            .ConfigureAwait(false);

        return subscription;
    }

    /// <summary>Adds a warehouse to a branch, optionally as its default.</summary>
    public static async Task<Warehouse> AddWarehouseAsync(
        DatabaseFixture fixture,
        Guid organizationId,
        Guid branchId,
        string code,
        bool isDefault = false,
        WarehouseType type = WarehouseType.Main)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        await using var scope = fixture.CreateScope(organizationId);
        var context = scope.ServiceProvider.GetRequiredService<RetailFlowDbContext>();
        var guids = scope.ServiceProvider.GetRequiredService<IGuidGenerator>();

        var warehouse = Warehouse.Create(
            guids.NewId(),
            organizationId,
            branchId,
            code,
            code + " Store",
            type,
            isDefault);

        context.Warehouses.Add(warehouse);
        await context.SaveChangesAsync().ConfigureAwait(false);

        return warehouse;
    }

    /// <summary>Adds a terminal to a branch.</summary>
    public static async Task<PosTerminal> AddTerminalAsync(
        DatabaseFixture fixture,
        Guid organizationId,
        Guid branchId,
        string code,
        Guid? defaultWarehouseId = null)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        await using var scope = fixture.CreateScope(organizationId);
        var context = scope.ServiceProvider.GetRequiredService<RetailFlowDbContext>();
        var guids = scope.ServiceProvider.GetRequiredService<IGuidGenerator>();

        var terminal = PosTerminal.Create(guids.NewId(), organizationId, branchId, code, code + " Counter");

        if (defaultWarehouseId is not null)
        {
            terminal.UpdateDetails(terminal.Name, defaultWarehouseId, null, null);
        }

        context.PosTerminals.Add(terminal);
        await context.SaveChangesAsync().ConfigureAwait(false);

        return terminal;
    }

    /// <summary>
    /// Makes a user a platform operator with <c>platform.tenants.manage</c>. Granted through a role
    /// like any other permission: there is no path that checks a flag instead (ADR-0013).
    /// </summary>
    public static async Task MakePlatformOperatorAsync(
        DatabaseFixture fixture,
        Guid organizationId,
        Guid userId)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        await using var scope = fixture.CreateScope(organizationId);
        var context = scope.ServiceProvider.GetRequiredService<RetailFlowDbContext>();
        var guids = scope.ServiceProvider.GetRequiredService<IGuidGenerator>();

        var role = Role.Create(guids.NewId(), organizationId, "Platform Operator", "Test operator");
        context.Roles.Add(role);
        context.RolePermissions.Add(RolePermission.Create(
            guids.NewId(), organizationId, role.Id, Permissions.Platform.ManageTenants));
        context.UserRoles.Add(UserRole.Create(guids.NewId(), organizationId, userId, role.Id));

        var user = await context.Users.FirstAsync(u => u.Id == userId).ConfigureAwait(false);
        user.MarkAsPlatformOperator();

        await context.SaveChangesAsync().ConfigureAwait(false);

        await scope.ServiceProvider.GetRequiredService<IUserSecurityStateProvider>()
            .InvalidateAsync(userId)
            .ConfigureAwait(false);
        await scope.ServiceProvider.GetRequiredService<IPermissionResolver>()
            .InvalidateAsync(userId)
            .ConfigureAwait(false);
    }

    /// <summary>Suspends a tenant through the domain, as the platform endpoint does.</summary>
    public static Task SuspendAsync(DatabaseFixture fixture, Guid organizationId, string reason = "test") =>
        MutateOrganizationAsync(fixture, organizationId, (organization, now) => organization.Suspend(now, reason));

    /// <summary>Closes a tenant: no surface at all, not even read-only.</summary>
    public static Task CloseAsync(DatabaseFixture fixture, Guid organizationId, string reason = "test") =>
        MutateOrganizationAsync(fixture, organizationId, (organization, now) => organization.Close(now, reason));

    private static async Task MutateOrganizationAsync(
        DatabaseFixture fixture,
        Guid organizationId,
        Action<Organization, DateTimeOffset> mutate)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        await using var scope = fixture.CreateScope(organizationId);
        var context = scope.ServiceProvider.GetRequiredService<RetailFlowDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();

        var organization = await context.Organizations
            .IgnoreQueryFilters()
            .FirstAsync(o => o.Id == organizationId)
            .ConfigureAwait(false);

        mutate(organization, clock.UtcNow);
        await context.SaveChangesAsync().ConfigureAwait(false);

        await scope.ServiceProvider.GetRequiredService<ITenantStatusProvider>()
            .InvalidateAsync(organizationId)
            .ConfigureAwait(false);
    }

    /// <summary>Signs in over HTTP and returns a client that carries the access token.</summary>
    public static async Task<HttpClient> SignedInClientAsync(
        ApiFactory factory,
        string organizationCode,
        string email,
        string password,
        Guid? branchId = null)
    {
        ArgumentNullException.ThrowIfNull(factory);

        var client = factory.CreateClient();

        var response = await client
            .PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(organizationCode, email, password, branchId))
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<AuthenticationResponse>().ConfigureAwait(false)
            ?? throw new InvalidOperationException("Sign-in returned no body.");

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.AccessToken);
        return client;
    }
}
