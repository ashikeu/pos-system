using FluentAssertions;
using RetailFlow.Domain.Platform;
using Xunit;

namespace RetailFlow.Domain.UnitTests.Platform;

public class ResultTests
{
    [Fact]
    public void Success_carries_a_value_and_no_error()
    {
        Result<int> result = 42;

        result.IsSuccess.Should().BeTrue();
        result.IsFailure.Should().BeFalse();
        result.Value.Should().Be(42);
        result.ErrorOrNull.Should().BeNull();
    }

    [Fact]
    public void Failure_carries_an_error_and_no_value()
    {
        Result<int> result = Error.NotFound("catalog.product_not_found", "Product not found.");

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("catalog.product_not_found");
        result.Error.Type.Should().Be(ErrorType.NotFound);

        var act = () => result.Value;
        act.Should().Throw<InvalidOperationException>().WithMessage("*catalog.product_not_found*");
    }

    [Fact]
    public void Reading_the_error_of_a_success_throws_rather_than_returning_a_placeholder()
    {
        var act = () => Result.Success().Error;

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Map_projects_a_success_and_passes_a_failure_through()
    {
        Result<int> success = 21;
        Result<int> failure = Error.Conflict("x.y", "nope");

        success.Map(v => v * 2).Value.Should().Be(42);
        failure.Map(v => v * 2).Error.Code.Should().Be("x.y");
    }

    [Fact]
    public void Validation_errors_can_carry_field_details()
    {
        var error = Error.Validation(
            "request.invalid",
            "The request is invalid.",
            new Dictionary<string, string[]> { ["lines[0].quantity"] = ["Quantity must be greater than zero."] });

        error.FieldErrors.Should().ContainKey("lines[0].quantity");
        error.Type.Should().Be(ErrorType.Validation);
    }

    [Theory]
    [InlineData("", "message")]
    [InlineData("code", "")]
    public void An_error_requires_a_code_and_a_message(string code, string message)
    {
        var act = () => new Error(code, message);

        act.Should().Throw<ArgumentException>();
    }
}
