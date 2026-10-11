using FluentAssertions;
using RetailFlow.Domain.Identity;
using Xunit;

namespace RetailFlow.Domain.UnitTests.Identity;

public class UserTests
{
    private static readonly Guid OrganizationId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 9, 0, 0, TimeSpan.Zero);

    private static User CreateUser() =>
        User.Create(Guid.NewGuid(), OrganizationId, "Cashier@Shop.Example", "Rumi Akter", "hash");

    [Fact]
    public void A_new_user_is_active_with_a_security_stamp_and_version_one()
    {
        var user = CreateUser();

        user.IsActive.Should().BeTrue();
        user.SecurityStamp.Should().NotBeNullOrWhiteSpace();
        user.PermissionsVersion.Should().Be(1);
        user.EmailConfirmed.Should().BeFalse();
    }

    [Fact]
    public void The_email_is_normalised_for_lookup_but_kept_as_entered_for_display()
    {
        var user = CreateUser();

        user.Email.Should().Be("Cashier@Shop.Example");
        user.NormalizedEmail.Should().Be("CASHIER@SHOP.EXAMPLE");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-at-sign")]
    [InlineData("@leading")]
    [InlineData("trailing@")]
    [InlineData("has space@example.com")]
    public void An_invalid_email_is_rejected(string email)
    {
        var act = () => User.Create(Guid.NewGuid(), OrganizationId, email, "Name", "hash");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_user_cannot_be_created_without_an_organization()
    {
        var act = () => User.Create(Guid.NewGuid(), Guid.Empty, "a@b.com", "Name", "hash");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Failed_attempts_below_the_threshold_do_not_lock_the_account()
    {
        var user = CreateUser();
        var policy = new PasswordPolicy(MaxFailedAttempts: 3);

        user.RecordLoginFailure(Now, policy).Should().BeFalse();
        user.RecordLoginFailure(Now, policy).Should().BeFalse();

        user.IsLockedOut(Now).Should().BeFalse();
        user.AccessFailedCount.Should().Be(2);
    }

    [Fact]
    public void The_account_locks_on_the_configured_attempt_and_raises_an_event()
    {
        var user = CreateUser();
        var policy = new PasswordPolicy(MaxFailedAttempts: 3, LockoutDuration: TimeSpan.FromMinutes(15));

        user.RecordLoginFailure(Now, policy);
        user.RecordLoginFailure(Now, policy);
        var locked = user.RecordLoginFailure(Now, policy);

        locked.Should().BeTrue();
        user.IsLockedOut(Now).Should().BeTrue();
        user.LockoutEndAtUtc.Should().Be(Now.AddMinutes(15));
        user.AccessFailedCount.Should().Be(0, "the counter restarts for the next lockout window");

        user.DomainEvents.Should().ContainSingle()
            .Which.Should().BeOfType<UserLockedOut>()
            .Which.LockoutCount.Should().Be(1);
    }

    [Fact]
    public void Repeated_lockouts_last_longer()
    {
        var user = CreateUser();
        var policy = new PasswordPolicy(MaxFailedAttempts: 1, LockoutDuration: TimeSpan.FromMinutes(15));

        user.RecordLoginFailure(Now, policy);
        var first = user.LockoutEndAtUtc!.Value - Now;

        user.RecordLoginFailure(Now, policy);
        var second = user.LockoutEndAtUtc!.Value - Now;

        first.Should().Be(TimeSpan.FromMinutes(15));
        second.Should().Be(TimeSpan.FromMinutes(30), "a password-guessing attempt must get slower");
    }

    [Fact]
    public void Escalation_can_be_switched_off()
    {
        var user = CreateUser();
        var policy = new PasswordPolicy(MaxFailedAttempts: 1, LockoutDuration: TimeSpan.FromMinutes(5), EscalateLockout: false);

        user.RecordLoginFailure(Now, policy);
        user.RecordLoginFailure(Now, policy);

        (user.LockoutEndAtUtc!.Value - Now).Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void A_lockout_expires_on_its_own()
    {
        var user = CreateUser();
        user.RecordLoginFailure(Now, new PasswordPolicy(MaxFailedAttempts: 1, LockoutDuration: TimeSpan.FromMinutes(15)));

        user.IsLockedOut(Now.AddMinutes(14)).Should().BeTrue();
        user.IsLockedOut(Now.AddMinutes(16)).Should().BeFalse();
    }

    [Fact]
    public void A_successful_sign_in_clears_the_failure_counters()
    {
        var user = CreateUser();
        user.RecordLoginFailure(Now, new PasswordPolicy(MaxFailedAttempts: 5));

        user.RecordLoginSuccess(Now);

        user.AccessFailedCount.Should().Be(0);
        user.LockoutEndAtUtc.Should().BeNull();
        user.LastLoginAtUtc.Should().Be(Now);
    }

    [Fact]
    public void Setting_a_password_rotates_the_security_stamp_and_unlocks_the_account()
    {
        var user = CreateUser();
        var originalStamp = user.SecurityStamp;
        user.RecordLoginFailure(Now, new PasswordPolicy(MaxFailedAttempts: 1));

        user.SetPassword("new-hash", Now);

        user.PasswordHash.Should().Be("new-hash");
        user.SecurityStamp.Should().NotBe(originalStamp, "existing access tokens must stop working");
        user.IsLockedOut(Now).Should().BeFalse();
        user.MustChangePassword.Should().BeFalse();
        user.PasswordChangedAtUtc.Should().Be(Now);
    }

    [Fact]
    public void Upgrading_a_hash_does_not_rotate_the_stamp()
    {
        var user = CreateUser();
        var stamp = user.SecurityStamp;

        user.UpgradePasswordHash("stronger-hash");

        user.PasswordHash.Should().Be("stronger-hash");
        user.SecurityStamp.Should().Be(stamp, "the credential did not change, only how it is stored");
    }

    [Fact]
    public void Deactivating_rotates_the_stamp_so_issued_tokens_stop_working()
    {
        var user = CreateUser();
        var stamp = user.SecurityStamp;

        user.Deactivate();

        user.IsActive.Should().BeFalse();
        user.SecurityStamp.Should().NotBe(stamp);
    }

    [Fact]
    public void Changing_the_email_requires_re_verification()
    {
        var user = CreateUser();
        user.ConfirmEmail();

        user.ChangeEmail("new@shop.example");

        user.EmailConfirmed.Should().BeFalse();
        user.NormalizedEmail.Should().Be("NEW@SHOP.EXAMPLE");
    }

    [Fact]
    public void Bumping_the_permissions_version_invalidates_cached_permission_sets()
    {
        var user = CreateUser();

        user.BumpPermissionsVersion();

        user.PermissionsVersion.Should().Be(2);
    }
}
