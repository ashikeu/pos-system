using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using RetailFlow.Application.Identity.Contracts;
using RetailFlow.Infrastructure.Identity.Services;
using RetailFlow.IntegrationTests.Infrastructure;
using Xunit;

namespace RetailFlow.IntegrationTests.Identity;

/// <summary>
/// Two-factor enrolment end to end, and the rate limit that protects the unauthenticated endpoints
/// (FR-SEC-06, FR-SEC-09).
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class MfaAndRateLimitTests : IAsyncLifetime
{
    private readonly DatabaseFixture _fixture;
    private ApiFactory? _factory;
    private TestTenant _tenant = null!;

    public MfaAndRateLimitTests(DatabaseFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(_fixture.ConnectionString);
        await IdentityTestData.EnsurePermissionCatalogAsync(_fixture).ConfigureAwait(false);
        _tenant = await IdentityTestData
            .CreateTenantAsync(_fixture, "MFA" + Random.Shared.Next(1000, 9999).ToString(System.Globalization.CultureInfo.InvariantCulture))
            .ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync().ConfigureAwait(false);
        }
    }

    private HttpClient Client => (_factory ?? throw new InvalidOperationException("Not initialised.")).CreateClient();

    [Fact]
    public async Task Enrolling_in_two_factor_then_signing_in_requires_a_code()
    {
        using var client = Client;
        var session = await SignInAsync(client, _tenant.OwnerEmail);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);

        // 1. Begin enrolment: a secret and a QR URI, but MFA is not on yet.
        var begin = await client.PostAsync("/api/v1/account/mfa", content: null);
        begin.StatusCode.Should().Be(HttpStatusCode.OK);

        var enrolment = (await begin.Content.ReadFromJsonAsync<MfaEnrolmentResponse>())!;
        enrolment.Secret.Should().MatchRegex("^[A-Z2-7]+$");
        enrolment.ProvisioningUri.Should().StartWith("otpauth://totp/");

        // A wrong code must not confirm enrolment.
        var wrong = await client.PostAsJsonAsync("/api/v1/account/mfa/confirm", new ConfirmMfaRequest("000000"));
        wrong.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCodeAsync(wrong)).Should().Be("identity.mfa_invalid");

        // 2. Confirm with a real code, as an authenticator app would produce it.
        var confirm = await client.PostAsJsonAsync(
            "/api/v1/account/mfa/confirm",
            new ConfirmMfaRequest(CurrentCode(enrolment.Secret)));

        confirm.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // 3. Signing in now needs the second factor.
        using var fresh = Client;
        var withoutCode = await fresh.PostAsJsonAsync(
            "/api/v1/auth/login",
            new LoginRequest(_tenant.OrganizationCode, _tenant.OwnerEmail, _tenant.Password));

        withoutCode.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCodeAsync(withoutCode)).Should().Be("identity.mfa_required");

        var withBadCode = await fresh.PostAsJsonAsync(
            "/api/v1/auth/login",
            new LoginRequest(_tenant.OrganizationCode, _tenant.OwnerEmail, _tenant.Password, MfaCode: "123456"));

        withBadCode.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCodeAsync(withBadCode)).Should().Be("identity.mfa_invalid");

        var withCode = await fresh.PostAsJsonAsync(
            "/api/v1/auth/login",
            new LoginRequest(
                _tenant.OrganizationCode,
                _tenant.OwnerEmail,
                _tenant.Password,
                MfaCode: CurrentCode(enrolment.Secret, stepOffset: 1)));

        withCode.StatusCode.Should().Be(
            HttpStatusCode.OK,
            await withCode.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Two_factor_cannot_be_turned_off_without_the_password()
    {
        using var client = Client;
        var session = await SignInAsync(client, _tenant.CashierEmail);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);

        var begin = await client.PostAsync("/api/v1/account/mfa", content: null);
        var enrolment = (await begin.Content.ReadFromJsonAsync<MfaEnrolmentResponse>())!;
        await client.PostAsJsonAsync("/api/v1/account/mfa/confirm", new ConfirmMfaRequest(CurrentCode(enrolment.Secret)));

        // A stolen access token must not be enough to remove the second factor.
        var wrongPassword = await client.PostAsJsonAsync(
            "/api/v1/account/mfa/disable",
            new DisableMfaRequest("NotMyPassw0rd!"));

        wrongPassword.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCodeAsync(wrongPassword)).Should().Be("identity.current_password_invalid");

        var correct = await client.PostAsJsonAsync(
            "/api/v1/account/mfa/disable",
            new DisableMfaRequest(_tenant.Password));

        correct.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Enrolling_twice_without_disabling_is_refused()
    {
        using var client = Client;
        var session = await SignInAsync(client, _tenant.OwnerEmail);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);

        var begin = await client.PostAsync("/api/v1/account/mfa", content: null);
        var enrolment = (await begin.Content.ReadFromJsonAsync<MfaEnrolmentResponse>())!;
        await client.PostAsJsonAsync("/api/v1/account/mfa/confirm", new ConfirmMfaRequest(CurrentCode(enrolment.Secret)));

        var again = await client.PostAsync("/api/v1/account/mfa", content: null);

        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorCodeAsync(again)).Should().Be("identity.mfa_already_enabled");
    }

    [Fact]
    public async Task The_sign_in_endpoint_is_rate_limited()
    {
        // Its own host with a deliberately tiny budget: the shared factory runs with a high limit
        // so the rest of the suite is not throttled.
        await using var throttled = new ApiFactory(_fixture.ConnectionString, authRequestsPerMinute: 3);
        using var client = throttled.CreateClient();

        var statuses = new List<HttpStatusCode>();

        for (var attempt = 0; attempt < 6; attempt++)
        {
            var response = await client.PostAsJsonAsync(
                "/api/v1/auth/login",
                new LoginRequest(_tenant.OrganizationCode, _tenant.OwnerEmail, "WrongPassw0rd!"));

            statuses.Add(response.StatusCode);
        }

        statuses.Should().Contain(HttpStatusCode.TooManyRequests);
        statuses.Take(3).Should().NotContain(HttpStatusCode.TooManyRequests, "the first attempts are within budget");
    }

    private async Task<AuthenticationResponse> SignInAsync(HttpClient client, string email)
    {
        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new LoginRequest(_tenant.OrganizationCode, email, _tenant.Password));

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        return (await response.Content.ReadFromJsonAsync<AuthenticationResponse>())!;
    }

    /// <param name="secret">The shared secret.</param>
    /// <param name="stepOffset">
    /// Which time step to compute for. A code is single-use, so after one has been accepted the
    /// next sign-in must use the following step's code — which is exactly what a user does when
    /// their authenticator rolls over, and what the +1 drift allowance exists for.
    /// </param>
    private static string CurrentCode(string secret, int stepOffset = 0) =>
        TotpService.ComputeForStep(
            TotpService.FromBase32(secret),
            (DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30) + stepOffset);

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}
