using FluentAssertions;
using RetailFlow.Domain.Platform;
using RetailFlow.Domain.Tenancy;
using Xunit;

namespace RetailFlow.Domain.UnitTests.Tenancy;

/// <summary>
/// The configuration chain (FR-TEN-04). These tests exist because precedence is the part of
/// settings that is easy to get subtly wrong, and getting it wrong means a till charging the wrong
/// tax or allowing the wrong discount.
/// </summary>
public sealed class SettingsResolutionTests
{
    private static readonly PlatformSettingsDefaults Defaults = new()
    {
        PriceIncludesTax = true,
        AllowNegativeStock = false,
        MaxLineDiscountPercent = 10m,
        ReturnWindowDays = 7,
        CashRoundingIncrement = 1.00m,
        CashRoundingMode = CashRoundingMode.Nearest,
        InvoiceNumberFormat = "{BRANCH}-{SEQ}",
        ReceiptTemplate58 = "platform-58",
        ReceiptTemplate80 = "platform-80",
    };

    private static readonly Guid OrganizationId = Guid.Parse("0193f000-0000-7000-8000-0000000000a1");
    private static readonly Guid BranchId = Guid.Parse("0193f000-0000-7000-8000-0000000000a2");

    [Fact]
    public void With_nothing_set_every_value_comes_from_the_platform_defaults()
    {
        var resolved = SettingsResolution.Resolve(Defaults, organization: null, branch: null, terminal: null);

        resolved.PriceIncludesTax.Should().BeTrue();
        resolved.AllowNegativeStock.Should().BeFalse();
        resolved.MaxLineDiscountPercent.Should().Be(10m);
        resolved.ReturnWindowDays.Should().Be(7);
        resolved.InvoiceNumberFormat.Should().Be("{BRANCH}-{SEQ}");
        resolved.ReceiptTemplate58.Should().Be("platform-58");
        resolved.DefaultWarehouseId.Should().BeNull();
    }

    [Fact]
    public void An_organization_override_beats_the_platform_default()
    {
        var organization = OrganizationSettings.CreateDefault(Guid.NewGuid(), OrganizationId);
        organization.Apply(new OrganizationSettingsValues(
            PriceIncludesTax: false,
            MaxLineDiscountPercent: 25m,
            ReturnWindowDays: 30));

        var resolved = SettingsResolution.Resolve(Defaults, organization, branch: null, terminal: null);

        resolved.PriceIncludesTax.Should().BeFalse();
        resolved.MaxLineDiscountPercent.Should().Be(25m);
        resolved.ReturnWindowDays.Should().Be(30);

        // Untouched settings keep following the platform, rather than being frozen at the value
        // the default happened to have when the tenant was created.
        resolved.CashRoundingIncrement.Should().Be(1.00m);
        resolved.ReceiptTemplate58.Should().Be("platform-58");
    }

    [Fact]
    public void A_branch_override_beats_the_organization()
    {
        var organization = OrganizationSettings.CreateDefault(Guid.NewGuid(), OrganizationId);
        organization.Apply(new OrganizationSettingsValues(
            MaxLineDiscountPercent: 25m,
            AllowNegativeStock: true,
            ReturnWindowDays: 30));

        var branch = BranchSettings.CreateDefault(Guid.NewGuid(), OrganizationId, BranchId);
        branch.Apply(new BranchSettingsValues(MaxLineDiscountPercent: 5m));

        var resolved = SettingsResolution.Resolve(Defaults, organization, branch, terminal: null);

        resolved.MaxLineDiscountPercent.Should().Be(5m, "the branch is the narrower scope");
        resolved.AllowNegativeStock.Should().BeTrue("the branch said nothing about it");
        resolved.ReturnWindowDays.Should().Be(30);
    }

    [Fact]
    public void A_cleared_branch_override_falls_back_to_the_organization()
    {
        var organization = OrganizationSettings.CreateDefault(Guid.NewGuid(), OrganizationId);
        organization.Apply(new OrganizationSettingsValues(MaxLineDiscountPercent: 25m));

        var branch = BranchSettings.CreateDefault(Guid.NewGuid(), OrganizationId, BranchId);
        branch.Apply(new BranchSettingsValues(MaxLineDiscountPercent: 5m));

        // Applying values is a replace, which is how a client expresses "stop overriding this".
        branch.Apply(new BranchSettingsValues());

        var resolved = SettingsResolution.Resolve(Defaults, organization, branch, terminal: null);

        resolved.MaxLineDiscountPercent.Should().Be(25m);
    }

    [Fact]
    public void A_terminal_template_beats_the_branch_and_the_organization()
    {
        var organization = OrganizationSettings.CreateDefault(Guid.NewGuid(), OrganizationId);
        organization.Apply(new OrganizationSettingsValues(ReceiptTemplate58: "org-58"));

        var branch = BranchSettings.CreateDefault(Guid.NewGuid(), OrganizationId, BranchId);
        branch.Apply(new BranchSettingsValues(ReceiptTemplate58: "branch-58", ReceiptTemplate80: "branch-80"));

        var terminal = PosTerminal.Create(Guid.NewGuid(), OrganizationId, BranchId, "POS1", "Counter 1");
        terminal.UpdateDetails(terminal.Name, defaultWarehouseId: null, "till-58", receiptTemplate80: null);

        var resolved = SettingsResolution.Resolve(Defaults, organization, branch, terminal);

        resolved.ReceiptTemplate58.Should().Be("till-58");
        resolved.ReceiptTemplate80.Should().Be("branch-80", "the terminal did not override the 80 mm template");
    }

    [Fact]
    public void Invoice_numbering_is_organization_wide_and_ignores_a_branch()
    {
        var organization = OrganizationSettings.CreateDefault(Guid.NewGuid(), OrganizationId);
        organization.Apply(new OrganizationSettingsValues(InvoiceNumberFormat: "INV-{YYYY}-{SEQ}"));

        var branch = BranchSettings.CreateDefault(Guid.NewGuid(), OrganizationId, BranchId);
        branch.Apply(new BranchSettingsValues(MaxLineDiscountPercent: 1m));

        var resolved = SettingsResolution.Resolve(Defaults, organization, branch, terminal: null);

        resolved.InvoiceNumberFormat.Should().Be("INV-{YYYY}-{SEQ}");
    }

    [Fact]
    public void The_default_warehouse_is_the_terminals_when_it_has_one_and_the_branchs_otherwise()
    {
        var branchWarehouse = Guid.Parse("0193f000-0000-7000-8000-0000000000b1");
        var terminalWarehouse = Guid.Parse("0193f000-0000-7000-8000-0000000000b2");

        var withoutTerminal = SettingsResolution.Resolve(
            Defaults, organization: null, branch: null, terminal: null, branchWarehouse);

        withoutTerminal.DefaultWarehouseId.Should().Be(branchWarehouse);

        var terminal = PosTerminal.Create(Guid.NewGuid(), OrganizationId, BranchId, "POS1", "Counter 1");
        terminal.UpdateDetails(terminal.Name, terminalWarehouse, null, null);

        var withTerminal = SettingsResolution.Resolve(
            Defaults, organization: null, branch: null, terminal, branchWarehouse);

        withTerminal.DefaultWarehouseId.Should().Be(terminalWarehouse);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(100.5)]
    public void A_discount_cap_outside_zero_to_one_hundred_is_refused(decimal percent)
    {
        var settings = OrganizationSettings.CreateDefault(Guid.NewGuid(), OrganizationId);

        var apply = () => settings.Apply(new OrganizationSettingsValues(MaxLineDiscountPercent: percent));

        apply.Should().Throw<DomainException>();
    }

    [Fact]
    public void A_negative_cash_rounding_increment_is_refused()
    {
        var settings = OrganizationSettings.CreateDefault(Guid.NewGuid(), OrganizationId);

        var apply = () => settings.Apply(new OrganizationSettingsValues(CashRoundingIncrement: -0.5m));

        apply.Should().Throw<DomainException>();
    }

    [Fact]
    public void A_zero_offline_window_is_refused_because_it_would_disable_offline_selling_silently()
    {
        var settings = OrganizationSettings.CreateDefault(Guid.NewGuid(), OrganizationId);

        var apply = () => settings.Apply(new OrganizationSettingsValues(OfflineMaxDurationHours: 0));

        apply.Should().Throw<DomainException>();
    }

    [Fact]
    public void A_blank_receipt_footer_is_treated_as_not_set()
    {
        var defaults = Defaults with { ReceiptFooterText = "   " };

        var resolved = SettingsResolution.Resolve(defaults, organization: null, branch: null, terminal: null);

        resolved.ReceiptFooterText.Should().BeNull(
            "a configuration provider turns a JSON null into an empty string, and an empty footer "
                + "would print a blank line on every receipt");
    }

    [Fact]
    public void Stored_values_round_trip_so_an_edit_screen_can_show_overrides_as_overrides()
    {
        var settings = BranchSettings.CreateDefault(Guid.NewGuid(), OrganizationId, BranchId);
        var values = new BranchSettingsValues(
            PriceIncludesTax: false,
            CashRoundingMode: CashRoundingMode.Down,
            OfflineMaxSaleAmount: 12_345.6789m,
            ReceiptFooterText: "  Thank you  ");

        settings.Apply(values);

        var stored = settings.ToValues();
        stored.PriceIncludesTax.Should().BeFalse();
        stored.CashRoundingMode.Should().Be(CashRoundingMode.Down);
        stored.OfflineMaxSaleAmount.Should().Be(12_345.6789m);
        stored.ReceiptFooterText.Should().Be("Thank you");
        stored.MaxLineDiscountPercent.Should().BeNull();
    }
}
