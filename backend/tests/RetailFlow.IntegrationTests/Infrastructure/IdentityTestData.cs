using Microsoft.Extensions.DependencyInjection;
using RetailFlow.Application.Identity.Abstractions;
using RetailFlow.Application.Platform.Abstractions;
using RetailFlow.Domain.Identity;
using RetailFlow.Domain.Tenancy;
using RetailFlow.Infrastructure.Identity.Persistence;
using RetailFlow.Infrastructure.Platform.Persistence;

namespace RetailFlow.IntegrationTests.Infrastructure;

/// <summary>A tenant set up for a test, with the accounts the test needs.</summary>
/// <param name="OrganizationId">The tenant.</param>
/// <param name="OrganizationCode">Its sign-in code.</param>
/// <param name="BranchId">Its main branch.</param>
/// <param name="SecondBranchId">A second branch, for branch-scope tests.</param>
/// <param name="OwnerUserId">A user with the Owner role.</param>
/// <param name="OwnerEmail">The owner's email address.</param>
/// <param name="CashierUserId">A user with the Cashier role.</param>
/// <param name="CashierEmail">The cashier's email address.</param>
/// <param name="Password">The password both accounts use.</param>
public sealed record TestTenant(
    Guid OrganizationId,
    string OrganizationCode,
    Guid BranchId,
    Guid SecondBranchId,
    Guid OwnerUserId,
    string OwnerEmail,
    Guid CashierUserId,
    string CashierEmail,
    string Password);

/// <summary>
/// Builds realistic tenants for identity tests: two branches, the system roles, an owner granted
/// the first branch and a cashier granted only the second. Tests that check branch scope and
/// permission enforcement need exactly this shape, and building it by hand in each test would make
/// the tests about setup rather than about the rule being verified.
/// </summary>
internal static class IdentityTestData
{
    public const string Password = "TestPassw0rd!";

    public static async Task<TestTenant> CreateTenantAsync(DatabaseFixture fixture, string code)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        var organization = await fixture.CreateOrganizationAsync(code).ConfigureAwait(false);

        await using var scope = fixture.CreateScope(organization.Id);
        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<RetailFlowDbContext>();
        var hasher = services.GetRequiredService<IPasswordHasher>();
        var guids = services.GetRequiredService<IGuidGenerator>();
        var seeder = services.GetRequiredService<IdentitySeeder>();

        var mainBranch = Branch.Create(guids.NewId(), organization.Id, "MAIN", code + " Main");
        var secondBranch = Branch.Create(guids.NewId(), organization.Id, "SECOND", code + " Second");
        context.Branches.AddRange(mainBranch, secondBranch);
        await context.SaveChangesAsync().ConfigureAwait(false);

        var roles = await seeder.CreateSystemRolesAsync(organization.Id).ConfigureAwait(false);
        var passwordHash = hasher.Hash(Password);

        var owner = User.Create(
            guids.NewId(),
            organization.Id,
            $"owner@{code.ToLowerInvariant()}.example",
            "Test Owner",
            passwordHash);
        owner.ConfirmEmail();

        var cashier = User.Create(
            guids.NewId(),
            organization.Id,
            $"cashier@{code.ToLowerInvariant()}.example",
            "Test Cashier",
            passwordHash);
        cashier.ConfirmEmail();

        context.Users.AddRange(owner, cashier);

        context.UserRoles.Add(UserRole.Create(guids.NewId(), organization.Id, owner.Id, roles[SystemRoles.Owner]));
        context.UserRoles.Add(UserRole.Create(guids.NewId(), organization.Id, cashier.Id, roles[SystemRoles.Cashier]));

        // The owner works in the main branch; the cashier only in the second one, so a test can
        // prove that asking for someone else's branch is refused.
        context.UserBranches.Add(UserBranch.Create(
            guids.NewId(), organization.Id, owner.Id, mainBranch.Id, roleId: null, isDefault: true));
        context.UserBranches.Add(UserBranch.Create(
            guids.NewId(), organization.Id, cashier.Id, secondBranch.Id, roleId: null, isDefault: true));

        await context.SaveChangesAsync().ConfigureAwait(false);

        return new TestTenant(
            organization.Id,
            organization.Code,
            mainBranch.Id,
            secondBranch.Id,
            owner.Id,
            owner.Email,
            cashier.Id,
            cashier.Email,
            Password);
    }

    /// <summary>Makes sure the permission catalog exists, as the API does on start-up.</summary>
    public static async Task EnsurePermissionCatalogAsync(DatabaseFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        await using var scope = fixture.CreateScope();
        await scope.ServiceProvider.GetRequiredService<PermissionCatalogSeeder>()
            .SyncAsync()
            .ConfigureAwait(false);
    }

    /// <summary>Applies a change to a user and clears the cached security state, as a handler would.</summary>
    public static async Task MutateUserAsync(DatabaseFixture fixture, TestTenant tenant, Guid userId, Action<User> mutate)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(mutate);

        await using var scope = fixture.CreateScope(tenant.OrganizationId);
        var context = scope.ServiceProvider.GetRequiredService<RetailFlowDbContext>();
        var user = await context.Users.FindAsync([userId]).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"User {userId} not found.");

        mutate(user);
        await context.SaveChangesAsync().ConfigureAwait(false);

        await scope.ServiceProvider.GetRequiredService<IUserSecurityStateProvider>()
            .InvalidateAsync(userId)
            .ConfigureAwait(false);
    }

    /// <summary>Suspends a tenant: still able to sign in, but read-only (FR-TEN-06, ADR-0030).</summary>
    public static Task SuspendOrganizationAsync(DatabaseFixture fixture, TestTenant tenant) =>
        SetOrganizationStateAsync(fixture, tenant, suspend: true);

    /// <summary>Closes a tenant: no surface at all, so sign-in is refused.</summary>
    public static Task CloseOrganizationAsync(DatabaseFixture fixture, TestTenant tenant) =>
        SetOrganizationStateAsync(fixture, tenant, suspend: false);

    private static async Task SetOrganizationStateAsync(DatabaseFixture fixture, TestTenant tenant, bool suspend)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(tenant);

        await using var scope = fixture.CreateScope(tenant.OrganizationId);
        var context = scope.ServiceProvider.GetRequiredService<RetailFlowDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();

        var organization = await context.Organizations.FindAsync([tenant.OrganizationId]).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Organization not found.");

        if (suspend)
        {
            organization.Suspend(clock.UtcNow, "integration test");
        }
        else
        {
            organization.Close(clock.UtcNow, "integration test");
        }

        await context.SaveChangesAsync().ConfigureAwait(false);
    }
}
