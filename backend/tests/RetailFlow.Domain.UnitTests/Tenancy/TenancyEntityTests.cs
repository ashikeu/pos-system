using FluentAssertions;
using RetailFlow.Domain.Platform;
using RetailFlow.Domain.Tenancy;
using Xunit;

namespace RetailFlow.Domain.UnitTests.Tenancy;

/// <summary>Invariants of the tenancy entities (FR-TEN-01, FR-TEN-06).</summary>
public sealed class TenancyEntityTests
{
    private static readonly Guid OrganizationId = Guid.Parse("0193f000-0000-7000-8000-0000000000c1");
    private static readonly Guid BranchId = Guid.Parse("0193f000-0000-7000-8000-0000000000c2");

    [Theory]
    [InlineData(WarehouseType.Damage)]
    [InlineData(WarehouseType.Transit)]
    public void A_damage_or_transit_warehouse_cannot_be_the_branch_default(WarehouseType type)
    {
        var create = () => Warehouse.Create(
            Guid.NewGuid(), OrganizationId, BranchId, "DMG", "Damaged stock", type, isDefault: true);

        create.Should().Throw<DomainException>(
            "sales would otherwise be posted out of a location nothing can be picked from");
    }

    [Fact]
    public void Deactivating_the_branch_default_warehouse_is_refused()
    {
        var warehouse = Warehouse.Create(
            Guid.NewGuid(), OrganizationId, BranchId, "MAIN", "Main store", WarehouseType.Main, isDefault: true);

        var deactivate = warehouse.Deactivate;

        deactivate.Should().Throw<DomainException>();

        warehouse.ClearDefault();
        warehouse.Deactivate();
        warehouse.IsActive.Should().BeFalse();
    }

    [Fact]
    public void An_inactive_warehouse_cannot_become_the_default()
    {
        var warehouse = Warehouse.Create(Guid.NewGuid(), OrganizationId, BranchId, "SPARE", "Spare");
        warehouse.Deactivate();

        var makeDefault = warehouse.MakeDefault;

        makeDefault.Should().Throw<DomainException>();
    }

    [Fact]
    public void A_warehouse_code_is_normalised_to_upper_case()
    {
        var warehouse = Warehouse.Create(Guid.NewGuid(), OrganizationId, BranchId, " main-2 ", " Main two ");

        warehouse.Code.Should().Be("MAIN-2");
        warehouse.Name.Should().Be("Main two");
    }

    [Fact]
    public void Registering_a_terminal_records_the_device_and_the_client_version()
    {
        var terminal = PosTerminal.Create(Guid.NewGuid(), OrganizationId, BranchId, "pos1", "Counter 1");
        var at = DateTimeOffset.Parse("2026-10-12T09:30:00Z", System.Globalization.CultureInfo.InvariantCulture);

        terminal.RecordRegistration(at, " 1.4.2 ", " machine-a ");

        terminal.Code.Should().Be("POS1");
        terminal.LastSyncAtUtc.Should().Be(at);
        terminal.AppVersion.Should().Be("1.4.2");
        terminal.DeviceFingerprint.Should().Be("machine-a");
    }

    [Fact]
    public void Suspending_an_organization_raises_an_event_and_records_the_reason()
    {
        var organization = Organization.Create(OrganizationId, "demo", "Demo Retail");
        var at = DateTimeOffset.Parse("2026-10-12T09:30:00Z", System.Globalization.CultureInfo.InvariantCulture);

        organization.Suspend(at, "payment overdue");

        organization.Status.Should().Be(OrganizationStatus.Suspended);
        organization.SuspendedAtUtc.Should().Be(at);
        organization.SuspendedReason.Should().Be("payment overdue");
        organization.DomainEvents.Should().ContainSingle()
            .Which.EventType.Should().Be("tenancy.organization-suspended");
    }

    [Fact]
    public void A_closed_organization_cannot_be_suspended_or_reactivated()
    {
        var organization = Organization.Create(OrganizationId, "demo", "Demo Retail");
        var at = DateTimeOffset.UtcNow;
        organization.Close(at, "shop sold");

        var suspend = () => organization.Suspend(at, "too late");
        var reactivate = () => organization.Reactivate(at);

        suspend.Should().Throw<DomainException>();
        reactivate.Should().Throw<DomainException>();
    }

    [Fact]
    public void An_organization_rejects_an_unsupported_language()
    {
        var organization = Organization.Create(OrganizationId, "demo", "Demo Retail");

        var setLocalization = () => organization.SetLocalization("Asia/Dhaka", "fr");

        setLocalization.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void An_organization_profile_is_trimmed_and_the_contact_address_lower_cased()
    {
        var organization = Organization.Create(OrganizationId, "demo", "Demo Retail");

        organization.UpdateProfile(
            " Demo Retail Ltd ", " ডেমো ", " 12 Road ", " Dhaka ", " +8801700000000 ", " Owner@Demo.Example ");

        organization.Name.Should().Be("Demo Retail Ltd");
        organization.NameBn.Should().Be("ডেমো");
        organization.AddressLine.Should().Be("12 Road");
        organization.City.Should().Be("Dhaka");
        organization.PhoneNumber.Should().Be("+8801700000000");
        organization.ContactEmail.Should().Be("owner@demo.example");
    }
}

/// <summary>Plan and subscription rules (FR-TEN-05, FR-TEN-06).</summary>
public sealed class SubscriptionTests
{
    private static readonly Guid OrganizationId = Guid.Parse("0193f000-0000-7000-8000-0000000000d1");
    private static readonly Guid PlanId = Guid.Parse("0193f000-0000-7000-8000-0000000000d2");
    private static readonly DateOnly Start = new(2026, 1, 1);

    [Fact]
    public void A_plan_with_a_trial_starts_trialing_and_its_period_ends_with_the_trial()
    {
        var subscription = OrganizationSubscription.Start(
            Guid.NewGuid(), OrganizationId, PlanId, Start, periodDays: 30, trialDays: 14);

        subscription.Status.Should().Be(SubscriptionStatus.Trialing);
        subscription.TrialEndsOn.Should().Be(Start.AddDays(14));
        subscription.CurrentPeriodEndsOn.Should().Be(Start.AddDays(14),
            "the same expiry check then covers a trial and a paid period");
    }

    [Fact]
    public void Writes_are_allowed_through_the_grace_period_and_refused_after_it()
    {
        var subscription = OrganizationSubscription.Start(
            Guid.NewGuid(), OrganizationId, PlanId, Start, periodDays: 30, trialDays: 0, graceDays: 7);

        subscription.CurrentPeriodEndsOn.Should().Be(Start.AddDays(30));
        subscription.WriteableUntil.Should().Be(Start.AddDays(37));

        subscription.AllowsWrites(Start.AddDays(30)).Should().BeTrue();
        subscription.AllowsWrites(Start.AddDays(37)).Should().BeTrue("the last day of grace still counts");
        subscription.AllowsWrites(Start.AddDays(38)).Should().BeFalse();
    }

    [Fact]
    public void An_expired_subscription_is_read_only_immediately_grace_or_not()
    {
        var subscription = OrganizationSubscription.Start(
            Guid.NewGuid(), OrganizationId, PlanId, Start, periodDays: 30, trialDays: 0, graceDays: 30);

        subscription.MarkExpired();

        subscription.AllowsWrites(Start.AddDays(1)).Should().BeFalse();
    }

    [Fact]
    public void A_cancelled_subscription_keeps_writing_until_the_period_it_paid_for_ends()
    {
        var subscription = OrganizationSubscription.Start(
            Guid.NewGuid(), OrganizationId, PlanId, Start, periodDays: 30, trialDays: 0, graceDays: 7);

        subscription.Cancel(DateTimeOffset.UtcNow, "closing the shop");

        subscription.AllowsWrites(Start.AddDays(30)).Should().BeTrue();
        subscription.AllowsWrites(Start.AddDays(31)).Should().BeFalse("grace is for late payment, not cancellation");
    }

    [Fact]
    public void A_renewal_must_extend_the_period_and_clears_a_late_state()
    {
        var subscription = OrganizationSubscription.Start(
            Guid.NewGuid(), OrganizationId, PlanId, Start, periodDays: 30);
        subscription.MarkPastDue();

        var shorten = () => subscription.Renew(Start.AddDays(10));
        shorten.Should().Throw<DomainException>();

        subscription.Renew(Start.AddDays(60));

        subscription.Status.Should().Be(SubscriptionStatus.Active);
        subscription.CurrentPeriodEndsOn.Should().Be(Start.AddDays(60));
    }

    [Fact]
    public void A_cancelled_subscription_cannot_change_plan()
    {
        var subscription = OrganizationSubscription.Start(
            Guid.NewGuid(), OrganizationId, PlanId, Start, periodDays: 30);
        subscription.Cancel(DateTimeOffset.UtcNow, "done");

        var change = () => subscription.ChangePlan(Guid.NewGuid());

        change.Should().Throw<DomainException>();
    }

    [Fact]
    public void Setting_a_feature_twice_replaces_the_limit_rather_than_adding_a_second_row()
    {
        var plan = SubscriptionPlan.Create(PlanId, "starter", "Starter", 1_500m, trialDays: 14);

        plan.SetFeature(Guid.NewGuid(), PlanFeatureCode.Branches, 1);
        plan.SetFeature(Guid.NewGuid(), PlanFeatureCode.Branches, 3);

        plan.Features.Should().ContainSingle()
            .Which.MaxCount.Should().Be(3);
    }

    [Fact]
    public void A_negative_feature_limit_or_price_is_refused()
    {
        var plan = SubscriptionPlan.Create(PlanId, "starter", "Starter", 1_500m);

        var negativeLimit = () => plan.SetFeature(Guid.NewGuid(), PlanFeatureCode.Users, -1);
        var negativePrice = () => SubscriptionPlan.Create(Guid.NewGuid(), "bad", "Bad", -1m);

        negativeLimit.Should().Throw<DomainException>();
        negativePrice.Should().Throw<DomainException>();
    }
}
