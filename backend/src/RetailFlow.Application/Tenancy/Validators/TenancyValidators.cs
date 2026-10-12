using FluentValidation;
using RetailFlow.Application.Tenancy.Contracts;

namespace RetailFlow.Application.Tenancy.Validators;

/// <summary>
/// Shared rules for the short codes that appear in document numbers and URLs. Letters, digits,
/// dash and underscore only: a code with a space or a slash in it breaks receipt layouts and
/// anything that embeds it in a path.
/// </summary>
internal static class CodeRules
{
    public const string Pattern = "^[A-Za-z0-9][A-Za-z0-9_-]{0,31}$";

    public const string Message =
        "A code must be 1–32 characters of letters, digits, dash or underscore, starting with a letter or digit.";

    public static IRuleBuilderOptions<T, string> Code<T>(this IRuleBuilder<T, string> builder) =>
        builder
            .NotEmpty()
            .MaximumLength(32)
            .Matches(Pattern)
            .WithMessage(Message);
}

internal sealed class ProvisionOrganizationRequestValidator : AbstractValidator<ProvisionOrganizationRequest>
{
    public ProvisionOrganizationRequestValidator()
    {
        RuleFor(r => r.IdempotencyKey).NotEmpty().MaximumLength(128);
        RuleFor(r => r.Code).Code();
        RuleFor(r => r.Name).NotEmpty().MaximumLength(200);
        RuleFor(r => r.OwnerEmail).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(r => r.OwnerFullName).NotEmpty().MaximumLength(200);

        // Length and content are checked by the password policy in the handler, which is the same
        // check the reset and change flows use; this only rejects an empty value early.
        RuleFor(r => r.OwnerPassword).NotEmpty();

        RuleFor(r => r.CurrencyCode)
            .Length(3)
            .When(r => !string.IsNullOrWhiteSpace(r.CurrencyCode));

        RuleFor(r => r.DefaultLanguage)
            .Must(language => language is "en" or "bn")
            .WithMessage("Supported languages are 'en' and 'bn'.")
            .When(r => !string.IsNullOrWhiteSpace(r.DefaultLanguage));

        RuleFor(r => r.LegalName).MaximumLength(200);
        RuleFor(r => r.BinNumber).MaximumLength(32);
        RuleFor(r => r.PhoneNumber).MaximumLength(32);
        RuleFor(r => r.AddressLine).MaximumLength(500);
        RuleFor(r => r.City).MaximumLength(100);
        RuleFor(r => r.BranchName).MaximumLength(200);
    }
}

internal sealed class UpdateOrganizationRequestValidator : AbstractValidator<UpdateOrganizationRequest>
{
    public UpdateOrganizationRequestValidator()
    {
        RuleFor(r => r.Name).NotEmpty().MaximumLength(200);
        RuleFor(r => r.NameBn).MaximumLength(200);
        RuleFor(r => r.LegalName).MaximumLength(200);
        RuleFor(r => r.BinNumber).MaximumLength(32);
        RuleFor(r => r.AddressLine).MaximumLength(500);
        RuleFor(r => r.City).MaximumLength(100);
        RuleFor(r => r.PhoneNumber).MaximumLength(32);

        RuleFor(r => r.ContactEmail)
            .EmailAddress()
            .MaximumLength(256)
            .When(r => !string.IsNullOrWhiteSpace(r.ContactEmail));

        RuleFor(r => r.DefaultLanguage)
            .Must(language => language is "en" or "bn")
            .WithMessage("Supported languages are 'en' and 'bn'.")
            .When(r => !string.IsNullOrWhiteSpace(r.DefaultLanguage));
    }
}

internal sealed class SuspendOrganizationRequestValidator : AbstractValidator<SuspendOrganizationRequest>
{
    public SuspendOrganizationRequestValidator()
    {
        RuleFor(r => r.OrganizationId).NotEmpty();
        RuleFor(r => r.Reason).NotEmpty().MaximumLength(500);
    }
}

internal sealed class ReactivateOrganizationRequestValidator : AbstractValidator<ReactivateOrganizationRequest>
{
    public ReactivateOrganizationRequestValidator() => RuleFor(r => r.OrganizationId).NotEmpty();
}

internal sealed class CreateBranchRequestValidator : AbstractValidator<CreateBranchRequest>
{
    public CreateBranchRequestValidator()
    {
        RuleFor(r => r.Code).Code();
        RuleFor(r => r.Name).NotEmpty().MaximumLength(200);
        RuleFor(r => r.NameBn).MaximumLength(200);
        RuleFor(r => r.AddressLine).MaximumLength(500);
        RuleFor(r => r.City).MaximumLength(100);
        RuleFor(r => r.PhoneNumber).MaximumLength(32);
        RuleFor(r => r.TimeZoneId).MaximumLength(64);
    }
}

internal sealed class UpdateBranchRequestValidator : AbstractValidator<UpdateBranchRequest>
{
    public UpdateBranchRequestValidator()
    {
        RuleFor(r => r.BranchId).NotEmpty();
        RuleFor(r => r.Name).NotEmpty().MaximumLength(200);
        RuleFor(r => r.NameBn).MaximumLength(200);
        RuleFor(r => r.AddressLine).MaximumLength(500);
        RuleFor(r => r.City).MaximumLength(100);
        RuleFor(r => r.PhoneNumber).MaximumLength(32);
        RuleFor(r => r.TimeZoneId).MaximumLength(64);
    }
}

internal sealed class SetBranchActivationRequestValidator : AbstractValidator<SetBranchActivationRequest>
{
    public SetBranchActivationRequestValidator() => RuleFor(r => r.BranchId).NotEmpty();
}

internal sealed class CreateWarehouseRequestValidator : AbstractValidator<CreateWarehouseRequest>
{
    public CreateWarehouseRequestValidator()
    {
        RuleFor(r => r.BranchId).NotEmpty();
        RuleFor(r => r.Code).Code();
        RuleFor(r => r.Name).NotEmpty().MaximumLength(200);
        RuleFor(r => r.Type).IsInEnum();
    }
}

internal sealed class UpdateWarehouseRequestValidator : AbstractValidator<UpdateWarehouseRequest>
{
    public UpdateWarehouseRequestValidator()
    {
        RuleFor(r => r.WarehouseId).NotEmpty();
        RuleFor(r => r.Name).NotEmpty().MaximumLength(200);
    }
}

internal sealed class SetWarehouseActivationRequestValidator : AbstractValidator<SetWarehouseActivationRequest>
{
    public SetWarehouseActivationRequestValidator() => RuleFor(r => r.WarehouseId).NotEmpty();
}

internal sealed class CreateTerminalRequestValidator : AbstractValidator<CreateTerminalRequest>
{
    public CreateTerminalRequestValidator()
    {
        RuleFor(r => r.BranchId).NotEmpty();
        RuleFor(r => r.Code).Code();
        RuleFor(r => r.Name).NotEmpty().MaximumLength(200);
        RuleFor(r => r.ReceiptTemplate58).MaximumLength(64);
        RuleFor(r => r.ReceiptTemplate80).MaximumLength(64);
    }
}

internal sealed class UpdateTerminalRequestValidator : AbstractValidator<UpdateTerminalRequest>
{
    public UpdateTerminalRequestValidator()
    {
        RuleFor(r => r.TerminalId).NotEmpty();
        RuleFor(r => r.Name).NotEmpty().MaximumLength(200);
        RuleFor(r => r.ReceiptTemplate58).MaximumLength(64);
        RuleFor(r => r.ReceiptTemplate80).MaximumLength(64);
    }
}

internal sealed class SetTerminalActivationRequestValidator : AbstractValidator<SetTerminalActivationRequest>
{
    public SetTerminalActivationRequestValidator() => RuleFor(r => r.TerminalId).NotEmpty();
}

internal sealed class RegisterTerminalRequestValidator : AbstractValidator<RegisterTerminalRequest>
{
    public RegisterTerminalRequestValidator()
    {
        RuleFor(r => r.IdempotencyKey).NotEmpty().MaximumLength(128);
        RuleFor(r => r.TerminalId).NotEmpty();
        RuleFor(r => r.DeviceName).NotEmpty().MaximumLength(200);
        RuleFor(r => r.AppVersion).MaximumLength(32);
        RuleFor(r => r.DeviceFingerprint).MaximumLength(256);
    }
}

internal sealed class UpdateBranchSettingsRequestValidator : AbstractValidator<UpdateBranchSettingsRequest>
{
    public UpdateBranchSettingsRequestValidator()
    {
        RuleFor(r => r.BranchId).NotEmpty();
        RuleFor(r => r.Values).NotNull();

        // The domain guards these too, and that guard stays as the last line. Checking them here
        // as well is what turns "422 invariant violated" into a 400 naming the field, which is
        // what a settings form needs to show the mistake next to the input (ADR-0014).
        When(r => r.Values is not null, () =>
        {
            RuleFor(r => r.Values.MaxLineDiscountPercent).InclusiveBetween(0m, 100m)
                .When(r => r.Values.MaxLineDiscountPercent is not null);
            RuleFor(r => r.Values.CashRoundingIncrement).GreaterThanOrEqualTo(0m)
                .When(r => r.Values.CashRoundingIncrement is not null);
            RuleFor(r => r.Values.VoidWindowMinutes).GreaterThanOrEqualTo(0)
                .When(r => r.Values.VoidWindowMinutes is not null);
            RuleFor(r => r.Values.ReturnWindowDays).GreaterThanOrEqualTo(0)
                .When(r => r.Values.ReturnWindowDays is not null);
            RuleFor(r => r.Values.OfflineMaxDurationHours).GreaterThan(0)
                .When(r => r.Values.OfflineMaxDurationHours is not null);
            RuleFor(r => r.Values.OfflineMaxSaleAmount).GreaterThanOrEqualTo(0m)
                .When(r => r.Values.OfflineMaxSaleAmount is not null);
            RuleFor(r => r.Values.OfflineMaxCumulativeAmount).GreaterThanOrEqualTo(0m)
                .When(r => r.Values.OfflineMaxCumulativeAmount is not null);
            RuleFor(r => r.Values.ReceiptTemplate58).MaximumLength(64);
            RuleFor(r => r.Values.ReceiptTemplate80).MaximumLength(64);
            RuleFor(r => r.Values.ReceiptFooterText).MaximumLength(500);
        });
    }
}

internal sealed class UpdateOrganizationSettingsRequestValidator
    : AbstractValidator<UpdateOrganizationSettingsRequest>
{
    public UpdateOrganizationSettingsRequestValidator()
    {
        RuleFor(r => r.Values).NotNull();

        When(r => r.Values is not null, () =>
        {
            RuleFor(r => r.Values.MaxLineDiscountPercent).InclusiveBetween(0m, 100m)
                .When(r => r.Values.MaxLineDiscountPercent is not null);
            RuleFor(r => r.Values.CashRoundingIncrement).GreaterThanOrEqualTo(0m)
                .When(r => r.Values.CashRoundingIncrement is not null);
            RuleFor(r => r.Values.VoidWindowMinutes).GreaterThanOrEqualTo(0)
                .When(r => r.Values.VoidWindowMinutes is not null);
            RuleFor(r => r.Values.ReturnWindowDays).GreaterThanOrEqualTo(0)
                .When(r => r.Values.ReturnWindowDays is not null);
            RuleFor(r => r.Values.OfflineMaxDurationHours).GreaterThan(0)
                .When(r => r.Values.OfflineMaxDurationHours is not null);
            RuleFor(r => r.Values.OfflineMaxSaleAmount).GreaterThanOrEqualTo(0m)
                .When(r => r.Values.OfflineMaxSaleAmount is not null);
            RuleFor(r => r.Values.OfflineMaxCumulativeAmount).GreaterThanOrEqualTo(0m)
                .When(r => r.Values.OfflineMaxCumulativeAmount is not null);
            RuleFor(r => r.Values.InvoiceNumberFormat).MaximumLength(64);
            RuleFor(r => r.Values.ReceiptTemplate58).MaximumLength(64);
            RuleFor(r => r.Values.ReceiptTemplate80).MaximumLength(64);
            RuleFor(r => r.Values.ReceiptFooterText).MaximumLength(500);
        });
    }
}

internal sealed class ChangePlanRequestValidator : AbstractValidator<ChangePlanRequest>
{
    public ChangePlanRequestValidator() => RuleFor(r => r.PlanCode).Code();
}

internal sealed class RenewSubscriptionRequestValidator : AbstractValidator<RenewSubscriptionRequest>
{
    public RenewSubscriptionRequestValidator()
    {
        RuleFor(r => r.OrganizationId).NotEmpty();

        RuleFor(r => r.PeriodEndsOn)
            .Must(date => date != default)
            .WithMessage("Give the date the new period ends.");
    }
}
