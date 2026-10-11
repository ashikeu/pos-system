using FluentAssertions;
using RetailFlow.Domain.Identity;
using Xunit;

namespace RetailFlow.Domain.UnitTests.Identity;

/// <summary>
/// The catalog and the role templates are the authorization contract (ADR-0013). These tests are
/// cheap insurance against the two mistakes that matter: a typo that makes a permission
/// unreachable, and a template that quietly grants more than intended.
/// </summary>
public class PermissionCatalogTests
{
    [Fact]
    public void Every_permission_code_is_unique()
    {
        var duplicates = Permissions.All
            .GroupBy(p => p.Code, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();

        duplicates.Should().BeEmpty();
    }

    [Fact]
    public void Every_permission_has_a_module_and_a_description()
    {
        Permissions.All.Should().OnlyContain(p =>
            !string.IsNullOrWhiteSpace(p.Module) && !string.IsNullOrWhiteSpace(p.Description));
    }

    [Fact]
    public void Codes_follow_the_module_dot_action_convention()
    {
        Permissions.All.Should().OnlyContain(p =>
            p.Code.StartsWith(p.Module + ".", StringComparison.Ordinal)
            && string.Equals(p.Code, p.Code.ToLowerInvariant(), StringComparison.Ordinal));
    }

    [Fact]
    public void Exists_recognises_a_real_code_and_rejects_an_invented_one()
    {
        Permissions.Exists(Permissions.Sales.Void).Should().BeTrue();
        Permissions.Exists("sales.do_whatever").Should().BeFalse();
    }

    [Fact]
    public void Every_role_template_grants_only_codes_that_exist()
    {
        foreach (var template in SystemRoles.All)
        {
            var unknown = template.PermissionCodes.Where(c => !Permissions.Exists(c)).ToArray();

            unknown.Should().BeEmpty($"role '{template.Name}' must not reference unknown permissions");
        }
    }

    [Fact]
    public void Every_role_template_grants_something_and_has_no_duplicates()
    {
        foreach (var template in SystemRoles.All)
        {
            template.PermissionCodes.Should().NotBeEmpty($"role '{template.Name}' would be useless");
            template.PermissionCodes.Should().OnlyHaveUniqueItems();
        }
    }

    [Fact]
    public void The_owner_gets_everything_except_platform_operator_permissions()
    {
        var owner = SystemRoles.For(SystemRoles.Owner);

        owner.PermissionCodes.Should().NotContain(Permissions.Platform.ManageTenants);
        owner.PermissionCodes.Should().NotContain(Permissions.Platform.ViewAuditLog);

        var tenantPermissions = Permissions.All
            .Where(p => p.Module != Permissions.Platform.Module)
            .Select(p => p.Code);

        owner.PermissionCodes.Should().BeEquivalentTo(tenantPermissions);
    }

    [Fact]
    public void A_cashier_can_sell_but_cannot_change_prices_or_approve_their_own_exceptions()
    {
        var cashier = SystemRoles.For(SystemRoles.Cashier);

        cashier.PermissionCodes.Should().Contain(Permissions.Sales.Create);
        cashier.PermissionCodes.Should().Contain(Permissions.Cash.OpenShift);
        cashier.PermissionCodes.Should().Contain(Permissions.Sync.Push);

        cashier.PermissionCodes.Should().NotContain(Permissions.Sales.OverridePrice);
        cashier.PermissionCodes.Should().NotContain(Permissions.Sales.Void);
        cashier.PermissionCodes.Should().NotContain(Permissions.Pricing.Manage);
        cashier.PermissionCodes.Should().NotContain(Permissions.Cash.ApproveVariance);
        cashier.PermissionCodes.Should().NotContain(Permissions.Inventory.Adjust);
        cashier.PermissionCodes.Should().NotContain(Permissions.Reports.ViewProfit);
    }

    [Fact]
    public void An_auditor_cannot_change_anything()
    {
        var auditor = SystemRoles.For(SystemRoles.Auditor);

        var mutating = auditor.PermissionCodes
            .Where(code => !code.EndsWith(".view", StringComparison.Ordinal)
                && !code.StartsWith(Permissions.Reports.Module + ".", StringComparison.Ordinal)
                && code != Permissions.Inventory.ViewCost)
            .ToArray();

        mutating.Should().BeEmpty("an auditor who can change records is not an auditor");
        auditor.PermissionCodes.Should().NotContain(Permissions.Sales.Create);
        auditor.PermissionCodes.Should().NotContain(Permissions.Reports.Export);
    }

    [Fact]
    public void A_manager_approves_exceptions_but_does_not_own_tax_or_subscription_settings()
    {
        var manager = SystemRoles.For(SystemRoles.Manager);

        manager.PermissionCodes.Should().Contain(Permissions.Sales.Void);
        manager.PermissionCodes.Should().Contain(Permissions.Returns.Approve);
        manager.PermissionCodes.Should().Contain(Permissions.Cash.ApproveVariance);
        manager.PermissionCodes.Should().Contain(Permissions.Inventory.ApproveAdjustment);

        manager.PermissionCodes.Should().NotContain(Permissions.Pricing.ManageTax);
        manager.PermissionCodes.Should().NotContain(Permissions.Organizations.ManageSubscription);
        manager.PermissionCodes.Should().NotContain(Permissions.Payments.ManageProviders);
    }

    [Fact]
    public void A_stock_keeper_cannot_sell_or_see_profit()
    {
        var keeper = SystemRoles.For(SystemRoles.StockKeeper);

        keeper.PermissionCodes.Should().Contain(Permissions.Procurement.ReceiveGoods);
        keeper.PermissionCodes.Should().NotContain(Permissions.Sales.Create);
        keeper.PermissionCodes.Should().NotContain(Permissions.Reports.ViewProfit);
    }

    [Fact]
    public void Asking_for_an_unknown_template_throws()
    {
        var act = () => SystemRoles.For("Supreme Leader");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_role_permission_cannot_reference_an_unknown_code()
    {
        var act = () => RolePermission.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "sales.invented");

        act.Should().Throw<Domain.Platform.DomainException>();
    }
}
