using System.Text;
using FluentAssertions;
using RetailFlow.Application.Identity.Abstractions;
using RetailFlow.Infrastructure.Identity.Services;
using Xunit;

namespace RetailFlow.IntegrationTests.Identity;

/// <summary>
/// The cryptographic primitives behind authentication, checked without a database. A quiet error
/// here would not fail a request — it would make passwords verifiable when they should not be, or
/// authenticator codes that no app can produce.
/// </summary>
public class SecurityPrimitiveTests
{
    private static AuthenticationOptions Options(int iterations = 120_000) => new()
    {
        PasswordIterations = iterations,
        SigningKey = "integration-test-signing-key-at-least-32-chars",
        SecretProtectionKey = Convert.ToBase64String(Encoding.UTF8.GetBytes("retailflow-test-secret-protection")[..32]),
    };

    [Fact]
    public void A_password_verifies_against_its_own_hash()
    {
        var hasher = new Pbkdf2PasswordHasher(Options());
        var hash = hasher.Hash("Correct Horse 7!");

        hasher.Verify(hash, "Correct Horse 7!").Should().Be(PasswordVerificationResult.Success);
    }

    [Fact]
    public void A_wrong_password_does_not_verify()
    {
        var hasher = new Pbkdf2PasswordHasher(Options());
        var hash = hasher.Hash("Correct Horse 7!");

        hasher.Verify(hash, "correct horse 7!").Should().Be(PasswordVerificationResult.Failed);
        hasher.Verify(hash, "Correct Horse 7").Should().Be(PasswordVerificationResult.Failed);
        hasher.Verify(hash, string.Empty).Should().Be(PasswordVerificationResult.Failed);
    }

    [Fact]
    public void Two_hashes_of_the_same_password_differ()
    {
        var hasher = new Pbkdf2PasswordHasher(Options());

        // Different salts, so a database leak cannot reveal that two users share a password.
        hasher.Hash("Same Password 1!").Should().NotBe(hasher.Hash("Same Password 1!"));
    }

    [Fact]
    public void A_hash_made_with_fewer_iterations_verifies_but_asks_to_be_upgraded()
    {
        var weak = new Pbkdf2PasswordHasher(Options(100_000)).Hash("Legacy Password 1!");
        var current = new Pbkdf2PasswordHasher(Options(200_000));

        current.Verify(weak, "Legacy Password 1!").Should().Be(PasswordVerificationResult.SuccessRehashNeeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("v1.notanumber.c2FsdA==.aGFzaA==")]
    [InlineData("v9.1000.c2FsdA==.aGFzaA==")]
    [InlineData("v1.1000.!!!not-base64!!!.aGFzaA==")]
    public void A_malformed_stored_hash_fails_verification_instead_of_throwing(string stored)
    {
        var hasher = new Pbkdf2PasswordHasher(Options());

        hasher.Verify(stored, "whatever").Should().Be(PasswordVerificationResult.Failed);
    }

    [Fact]
    public void A_misconfigured_iteration_count_is_floored_rather_than_trusted()
    {
        var hasher = new Pbkdf2PasswordHasher(Options(iterations: 1));
        var hash = hasher.Hash("Floored Password 1!");

        hash.Split('.')[1].Should().Be("100000");
    }

    [Fact]
    public void Generated_tokens_are_unique_url_safe_and_long_enough()
    {
        var generator = new SecureTokenGenerator();
        var tokens = Enumerable.Range(0, 1_000).Select(_ => generator.CreateToken()).ToArray();

        tokens.Should().OnlyHaveUniqueItems();
        tokens.Should().OnlyContain(t => t.Length >= 40);
        tokens.Should().OnlyContain(t => !t.Contains('+', StringComparison.Ordinal)
            && !t.Contains('/', StringComparison.Ordinal)
            && !t.Contains('=', StringComparison.Ordinal));
    }

    [Fact]
    public void Hashing_a_token_is_deterministic_and_hides_the_value()
    {
        var generator = new SecureTokenGenerator();
        var token = generator.CreateToken();

        var hash = generator.Hash(token);

        hash.Should().Be(generator.Hash(token));
        hash.Should().HaveLength(64).And.NotContain(token);
    }

    [Fact]
    public void Numeric_codes_have_the_requested_length()
    {
        var generator = new SecureTokenGenerator();

        generator.CreateNumericCode(6).Should().HaveLength(6).And.MatchRegex("^[0-9]{6}$");
        generator.CreateNumericCode(4).Should().HaveLength(4);
    }

    [Fact]
    public void A_protected_secret_round_trips_and_is_not_readable_as_stored()
    {
        var protector = new AesGcmSecretProtector(Options());
        const string secret = "JBSWY3DPEHPK3PXP";

        var sealedValue = protector.Protect(secret);

        sealedValue.Should().NotContain(secret);
        protector.Unprotect(sealedValue).Should().Be(secret);
    }

    [Fact]
    public void Protecting_the_same_value_twice_gives_different_ciphertext()
    {
        var protector = new AesGcmSecretProtector(Options());

        // Random nonce per value: identical secrets must not be identifiable in the database.
        protector.Protect("same").Should().NotBe(protector.Protect("same"));
    }

    [Fact]
    public void A_tampered_secret_is_rejected_rather_than_silently_decrypted()
    {
        var protector = new AesGcmSecretProtector(Options());
        var sealedValue = protector.Protect("JBSWY3DPEHPK3PXP");

        var bytes = Convert.FromBase64String(sealedValue);
        bytes[^1] ^= 0xFF;

        var act = () => protector.Unprotect(Convert.ToBase64String(bytes));

        act.Should().Throw<System.Security.Cryptography.CryptographicException>();
    }

    [Fact]
    public void A_missing_protection_key_fails_at_construction()
    {
        var act = () => new AesGcmSecretProtector(new AuthenticationOptions());

        act.Should().Throw<InvalidOperationException>().WithMessage("*SecretProtectionKey*");
    }

    /// <summary>
    /// RFC 6238 Appendix B test vectors for HMAC-SHA1. The published vectors use an 8-digit code;
    /// this implementation issues 6, so the expected values are the last six digits — which is what
    /// authenticator apps display.
    /// </summary>
    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    public void Totp_matches_the_RFC_6238_vectors(long unixSeconds, string expectedCode)
    {
        var key = Encoding.ASCII.GetBytes("12345678901234567890");
        var timeStep = unixSeconds / 30;

        TotpService.ComputeForStep(key, timeStep).Should().Be(expectedCode);
    }

    [Fact]
    public void A_generated_secret_is_valid_base32_of_160_bits()
    {
        var service = new TotpService();

        var secret = service.CreateSecret();

        secret.Should().MatchRegex("^[A-Z2-7]+$");
        TotpService.FromBase32(secret).Should().HaveCount(20);
    }

    [Fact]
    public void A_current_code_validates_and_a_wrong_one_does_not()
    {
        var service = new TotpService();
        var secret = service.CreateSecret();
        var now = DateTimeOffset.UtcNow;
        var code = TotpService.ComputeForStep(TotpService.FromBase32(secret), now.ToUnixTimeSeconds() / 30);

        service.TryValidate(secret, code, now, lastUsedTimeStep: 0, out var step).Should().BeTrue();
        step.Should().Be(now.ToUnixTimeSeconds() / 30);

        service.TryValidate(secret, "000000", now, 0, out _).Should().BeFalse();
    }

    [Fact]
    public void A_code_from_the_previous_step_is_accepted_for_clock_drift()
    {
        var service = new TotpService();
        var secret = service.CreateSecret();
        var now = DateTimeOffset.UtcNow;
        var previousStep = (now.ToUnixTimeSeconds() / 30) - 1;
        var code = TotpService.ComputeForStep(TotpService.FromBase32(secret), previousStep);

        service.TryValidate(secret, code, now, lastUsedTimeStep: 0, out var step).Should().BeTrue();
        step.Should().Be(previousStep);
    }

    [Fact]
    public void A_code_from_an_already_used_step_is_refused()
    {
        var service = new TotpService();
        var secret = service.CreateSecret();
        var now = DateTimeOffset.UtcNow;
        var currentStep = now.ToUnixTimeSeconds() / 30;
        var code = TotpService.ComputeForStep(TotpService.FromBase32(secret), currentStep);

        // Replay protection: the same code cannot be used twice inside its window.
        service.TryValidate(secret, code, now, lastUsedTimeStep: currentStep, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("abcdef")]
    [InlineData("")]
    public void A_malformed_code_is_refused_without_throwing(string code)
    {
        var service = new TotpService();

        service.TryValidate(service.CreateSecret(), code, DateTimeOffset.UtcNow, 0, out _).Should().BeFalse();
    }

    [Fact]
    public void The_provisioning_uri_carries_what_an_authenticator_app_needs()
    {
        var service = new TotpService();

        var uri = service.BuildProvisioningUri("JBSWY3DPEHPK3PXP", "RetailFlow", "owner@demo.local");

        uri.Should().StartWith("otpauth://totp/");
        uri.Should().Contain("secret=JBSWY3DPEHPK3PXP");
        uri.Should().Contain("digits=6").And.Contain("period=30").And.Contain("algorithm=SHA1");
        uri.Should().Contain("owner%40demo.local");
    }
}
