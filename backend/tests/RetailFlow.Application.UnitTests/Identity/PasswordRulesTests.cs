using FluentAssertions;
using RetailFlow.Application.Identity.Abstractions;
using RetailFlow.Application.Identity.UseCases;
using Xunit;

namespace RetailFlow.Application.UnitTests.Identity;

public class PasswordRulesTests
{
    private const int MinimumLength = 8;

    [Theory]
    [InlineData("Shop2026!")]
    [InlineData("till-counter-9")]
    [InlineData("দোকান2026!")]
    public void A_password_with_length_and_variety_is_accepted(string password) =>
        PasswordRules.Validate(password, MinimumLength).IsSuccess.Should().BeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_password_is_refused(string? password)
    {
        var result = PasswordRules.Validate(password, MinimumLength);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("identity.password_policy");
    }

    [Fact]
    public void A_short_password_is_refused_with_the_required_length_in_the_message()
    {
        var result = PasswordRules.Validate("Sh0rt!", MinimumLength);

        result.IsFailure.Should().BeTrue();
        result.Error.Message.Should().Contain("8");
    }

    [Theory]
    [InlineData("alphabetsonly")]
    [InlineData("1234567890")]
    public void Letters_alone_or_digits_alone_are_refused(string password) =>
        PasswordRules.Validate(password, MinimumLength).IsFailure.Should().BeTrue();

    [Theory]
    [InlineData("password")]
    [InlineData("Password123")]
    [InlineData("retailflow")]
    [InlineData("POS12345")]
    public void Obviously_common_choices_are_refused(string password) =>
        PasswordRules.Validate(password, MinimumLength).IsFailure.Should().BeTrue();

    [Fact]
    public void An_extremely_long_password_is_refused_so_hashing_cannot_be_abused()
    {
        var result = PasswordRules.Validate(new string('a', 300) + "1!", MinimumLength);

        result.IsFailure.Should().BeTrue();
        result.Error.Message.Should().Contain("too long");
    }
}

public class ResolveBranchTests
{
    private static readonly Guid MainBranch = Guid.NewGuid();
    private static readonly Guid SecondBranch = Guid.NewGuid();

    private static BranchGrant Grant(Guid id, bool isDefault = false) =>
        new(id, "CODE", "Name", isDefault, RoleId: null);

    [Fact]
    public void The_requested_branch_is_used_when_it_is_granted()
    {
        var result = AuthenticationIssuer.ResolveBranch([Grant(MainBranch), Grant(SecondBranch)], SecondBranch);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(SecondBranch);
    }

    [Fact]
    public void A_branch_that_is_not_granted_is_refused()
    {
        var result = AuthenticationIssuer.ResolveBranch([Grant(MainBranch)], SecondBranch);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("identity.branch_not_granted");
        result.Error.Type.Should().Be(Domain.Platform.ErrorType.Forbidden);
    }

    [Fact]
    public void Without_a_request_the_default_branch_wins()
    {
        var result = AuthenticationIssuer.ResolveBranch(
            [Grant(MainBranch), Grant(SecondBranch, isDefault: true)],
            requestedBranchId: null);

        result.Value.Should().Be(SecondBranch);
    }

    [Fact]
    public void Without_a_default_the_first_grant_is_used()
    {
        var result = AuthenticationIssuer.ResolveBranch([Grant(MainBranch), Grant(SecondBranch)], null);

        result.Value.Should().Be(MainBranch);
    }

    [Fact]
    public void With_no_grants_the_scope_is_organization_wide()
    {
        var result = AuthenticationIssuer.ResolveBranch([], null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
    }
}
