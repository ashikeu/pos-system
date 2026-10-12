using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using RetailFlow.Application.Identity.Contracts;
using RetailFlow.Domain.Identity;
using RetailFlow.IntegrationTests.Infrastructure;
using Xunit;

namespace RetailFlow.IntegrationTests.Identity;

/// <summary>
/// Sign-in, rotation, revocation and authorization over real HTTP against the real middleware
/// pipeline (FR-SEC-01…10). These are the tests that would catch the failures that matter most:
/// a stolen token that keeps working, a cashier who can reach a manager's endpoint, or one shop
/// seeing another's accounts.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class AuthenticationFlowTests : IAsyncLifetime
{
    private readonly DatabaseFixture _fixture;
    private ApiFactory? _factory;
    private TestTenant _tenant = null!;

    public AuthenticationFlowTests(DatabaseFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(_fixture.ConnectionString);
        await IdentityTestData.EnsurePermissionCatalogAsync(_fixture).ConfigureAwait(false);
        _tenant = await IdentityTestData
            .CreateTenantAsync(_fixture, "AUTH" + Random.Shared.Next(1000, 9999).ToString(System.Globalization.CultureInfo.InvariantCulture))
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

    // ---------------------------------------------------------------- sign-in

    [Fact]
    public async Task A_valid_sign_in_returns_tokens_scope_and_permissions()
    {
        using var client = Client;

        var response = await LoginAsync(client, _tenant.OwnerEmail);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<AuthenticationResponse>();
        body.Should().NotBeNull();
        body!.AccessToken.Should().NotBeNullOrWhiteSpace();
        body.RefreshToken.Should().NotBeNullOrWhiteSpace();
        body.ExpiresInSeconds.Should().BePositive();
        body.RefreshTokenExpiresAtUtc.Should().BeAfter(DateTimeOffset.UtcNow);

        body.User.OrganizationId.Should().Be(_tenant.OrganizationId);
        body.User.BranchId.Should().Be(_tenant.BranchId, "the owner's default branch");
        body.User.Roles.Should().Contain(SystemRoles.Owner);
        body.User.Permissions.Should().Contain(Permissions.Sales.Void);
        body.User.AccessibleBranches.Should().ContainSingle(b => b.BranchId == _tenant.BranchId);
        body.User.SessionId.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public async Task A_cashier_gets_only_the_cashier_permission_set()
    {
        using var client = Client;

        var body = await LoginAndReadAsync(client, _tenant.CashierEmail);

        body.User.Roles.Should().Contain(SystemRoles.Cashier);
        body.User.Permissions.Should().Contain(Permissions.Sales.Create);
        body.User.Permissions.Should().NotContain(Permissions.Sales.Void);
        body.User.Permissions.Should().NotContain(Permissions.Users.ViewSessions);
        body.User.BranchId.Should().Be(_tenant.SecondBranchId);
    }

    [Fact]
    public async Task A_wrong_password_is_reported_as_invalid_credentials()
    {
        using var client = Client;

        var response = await LoginAsync(client, _tenant.OwnerEmail, password: "WrongPassw0rd!");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCodeAsync(response)).Should().Be("identity.invalid_credentials");
    }

    [Fact]
    public async Task An_unknown_email_looks_exactly_like_a_wrong_password()
    {
        using var client = Client;

        var response = await LoginAsync(client, "nobody@nowhere.example");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCodeAsync(response)).Should().Be(
            "identity.invalid_credentials",
            "the endpoint must not reveal whether an account exists");
    }

    [Fact]
    public async Task An_unknown_organization_code_looks_the_same_too()
    {
        using var client = Client;

        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new LoginRequest("NOSUCHORG", _tenant.OwnerEmail, _tenant.Password));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCodeAsync(response)).Should().Be("identity.invalid_credentials");
    }

    [Fact]
    public async Task Repeated_wrong_passwords_lock_the_account()
    {
        var tenant = await IdentityTestData.CreateTenantAsync(_fixture, "LOCK" + Random.Shared.Next(1000, 9999));
        using var client = Client;

        // The configured threshold is five failures.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await LoginAsync(client, tenant.OwnerEmail, password: "WrongPassw0rd!", organizationCode: tenant.OrganizationCode);
        }

        var locked = await LoginAsync(client, tenant.OwnerEmail, organizationCode: tenant.OrganizationCode);

        locked.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(locked)).Should().Be(
            "identity.account_locked",
            "the correct password must not work while the account is locked");
    }

    [Fact]
    public async Task A_deactivated_account_cannot_sign_in()
    {
        await IdentityTestData.MutateUserAsync(_fixture, _tenant, _tenant.CashierUserId, u => u.Deactivate());
        using var client = Client;

        var response = await LoginAsync(client, _tenant.CashierEmail);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(response)).Should().Be("identity.account_inactive");
    }

    [Fact]
    public async Task A_suspended_organization_can_still_sign_in_because_it_gets_a_read_only_surface()
    {
        var tenant = await IdentityTestData.CreateTenantAsync(_fixture, "SUSP" + Random.Shared.Next(1000, 9999));
        await IdentityTestData.SuspendOrganizationAsync(_fixture, tenant);
        using var client = Client;

        var response = await LoginAsync(client, tenant.OwnerEmail, organizationCode: tenant.OrganizationCode);

        // Suspension is a billing state, not a security one: the shop keeps read access and the
        // renewal endpoints, and writes are refused per request by the tenant write gate
        // (FR-TEN-06, ADR-0030). The write side of this is covered by TenancyEndpointTests.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_closed_organization_cannot_sign_in()
    {
        var tenant = await IdentityTestData.CreateTenantAsync(_fixture, "CLSD" + Random.Shared.Next(1000, 9999));
        await IdentityTestData.CloseOrganizationAsync(_fixture, tenant);
        using var client = Client;

        var response = await LoginAsync(client, tenant.OwnerEmail, organizationCode: tenant.OrganizationCode);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(response)).Should().Be("identity.organization_unavailable");
    }

    [Fact]
    public async Task Asking_for_a_branch_the_user_does_not_have_is_refused()
    {
        using var client = Client;

        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new LoginRequest(
                _tenant.OrganizationCode,
                _tenant.CashierEmail,
                _tenant.Password,
                BranchId: _tenant.BranchId));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(response)).Should().Be("identity.branch_not_granted");
    }

    [Fact]
    public async Task Sign_in_attempts_are_recorded_in_login_history()
    {
        using var client = Client;
        await LoginAsync(client, _tenant.OwnerEmail, password: "WrongPassw0rd!");
        var session = await LoginAndReadAsync(client, _tenant.OwnerEmail);

        using var authorised = Client;
        authorised.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);

        var history = await authorised.GetFromJsonAsync<List<LoginHistoryEntry>>("/api/v1/account/login-history");

        history.Should().NotBeNull();
        history!.Should().Contain(e => e.Succeeded);
        history.Should().Contain(e => !e.Succeeded && e.FailureReason == nameof(LoginFailureReason.WrongPassword));
    }

    // ---------------------------------------------------------------- rotation and revocation

    [Fact]
    public async Task Refreshing_rotates_both_tokens()
    {
        using var client = Client;
        var first = await LoginAndReadAsync(client, _tenant.OwnerEmail);

        var response = await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(first.RefreshToken));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await response.Content.ReadFromJsonAsync<AuthenticationResponse>();
        second.Should().NotBeNull();
        second!.RefreshToken.Should().NotBe(first.RefreshToken, "a refresh token is single use");
        second.User.SessionId.Should().Be(first.User.SessionId, "the session continues");
    }

    [Fact]
    public async Task Replaying_a_used_refresh_token_ends_the_whole_session()
    {
        using var client = Client;
        var first = await LoginAndReadAsync(client, _tenant.OwnerEmail);

        var refreshed = await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(first.RefreshToken));
        var second = (await refreshed.Content.ReadFromJsonAsync<AuthenticationResponse>())!;

        // The old value is presented again — what a stolen token looks like.
        var replay = await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(first.RefreshToken));

        replay.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(replay)).Should().Be("identity.refresh_token_reused");

        // And the token the legitimate client holds is revoked too: the family is burned.
        var afterReuse = await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(second.RefreshToken));

        afterReuse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCodeAsync(afterReuse)).Should().Be("identity.refresh_token_invalid");
    }

    [Fact]
    public async Task Signing_out_stops_the_refresh_token_working()
    {
        using var client = Client;
        var session = await LoginAndReadAsync(client, _tenant.OwnerEmail);

        var logout = await client.PostAsJsonAsync("/api/v1/auth/logout", new LogoutRequest(session.RefreshToken));
        logout.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var refresh = await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(session.RefreshToken));
        refresh.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Signing_out_everywhere_invalidates_tokens_that_have_not_expired()
    {
        using var client = Client;
        var session = await LoginAndReadAsync(client, _tenant.CashierEmail);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);

        (await client.GetAsync("/api/v1/account/me")).StatusCode.Should().Be(HttpStatusCode.OK);

        var revoked = await client.PostAsync("/api/v1/account/logout-all", content: null);
        revoked.StatusCode.Should().Be(HttpStatusCode.OK);

        // The access token is still within its lifetime, but the security stamp moved.
        var afterRevoke = await client.GetAsync("/api/v1/account/me");

        afterRevoke.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(afterRevoke)).Should().Be("identity.session_superseded");
    }

    [Fact]
    public async Task A_garbage_token_is_rejected_as_unauthenticated()
    {
        using var client = Client;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not.a.real.token");

        var response = await client.GetAsync("/api/v1/account/me");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_token_with_a_tampered_signature_is_rejected()
    {
        using var client = Client;
        var session = await LoginAndReadAsync(client, _tenant.OwnerEmail);

        var parts = session.AccessToken.Split('.');
        var tampered = string.Join('.', parts[0], parts[1], new string(parts[2].Reverse().ToArray()));

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tampered);

        (await client.GetAsync("/api/v1/account/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_endpoint_requires_authentication()
    {
        using var client = Client;

        (await client.GetAsync("/api/v1/account/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---------------------------------------------------------------- authorization

    [Fact]
    public async Task A_permission_the_role_lacks_is_refused_server_side()
    {
        using var client = Client;
        var session = await LoginAndReadAsync(client, _tenant.CashierEmail);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);

        // The client hid the button; the server refuses anyway (FR-SEC-10).
        var response = await client.GetAsync($"/api/v1/users/{_tenant.OwnerUserId}/sessions");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_same_endpoint_is_allowed_for_a_role_that_has_the_permission()
    {
        using var client = Client;
        var session = await LoginAndReadAsync(client, _tenant.OwnerEmail);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);

        var response = await client.GetAsync($"/api/v1/users/{_tenant.CashierUserId}/sessions");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_branch_header_for_an_ungranted_branch_is_refused()
    {
        using var client = Client;
        var session = await LoginAndReadAsync(client, _tenant.CashierEmail);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        client.DefaultRequestHeaders.Add("X-Branch-Id", _tenant.BranchId.ToString());

        var response = await client.GetAsync("/api/v1/account/me");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(response)).Should().Be("identity.branch_not_granted");
    }

    [Fact]
    public async Task A_branch_header_for_a_granted_branch_sets_the_scope()
    {
        using var client = Client;
        var session = await LoginAndReadAsync(client, _tenant.CashierEmail);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        client.DefaultRequestHeaders.Add("X-Branch-Id", _tenant.SecondBranchId.ToString());

        var response = await client.GetAsync("/api/v1/account/me");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("branchId").GetGuid().Should().Be(_tenant.SecondBranchId);
    }

    [Fact]
    public async Task A_user_of_one_tenant_cannot_see_another_tenants_sessions()
    {
        var other = await IdentityTestData.CreateTenantAsync(_fixture, "XTEN" + Random.Shared.Next(1000, 9999));

        using var client = Client;
        var session = await LoginAndReadAsync(client, _tenant.OwnerEmail);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);

        // The permission is held, but the user belongs to another organization: the tenant filter
        // makes them invisible, so this is an empty list rather than a leak (FR-TEN-02).
        var response = await client.GetAsync($"/api/v1/users/{other.OwnerUserId}/sessions");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<List<SessionSummary>>()).Should().BeEmpty();
    }

    // ---------------------------------------------------------------- password and sessions

    [Fact]
    public async Task Changing_the_password_replaces_the_credential()
    {
        var tenant = await IdentityTestData.CreateTenantAsync(_fixture, "PWCH" + Random.Shared.Next(1000, 9999));
        const string newPassword = "Replacem3nt!2026";

        using var client = Client;
        var session = await LoginAndReadAsync(client, tenant.OwnerEmail, tenant.OrganizationCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);

        var changed = await client.PostAsJsonAsync(
            "/api/v1/account/password",
            new ChangePasswordRequest(tenant.Password, newPassword));

        changed.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var fresh = Client;
        (await LoginAsync(fresh, tenant.OwnerEmail, tenant.Password, tenant.OrganizationCode))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "the old password must stop working");
        (await LoginAsync(fresh, tenant.OwnerEmail, newPassword, tenant.OrganizationCode))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_password_change_with_the_wrong_current_password_is_refused()
    {
        using var client = Client;
        var session = await LoginAndReadAsync(client, _tenant.OwnerEmail);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);

        var response = await client.PostAsJsonAsync(
            "/api/v1/account/password",
            new ChangePasswordRequest("NotMyPassw0rd!", "Replacem3nt!2026"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCodeAsync(response)).Should().Be("identity.current_password_invalid");
    }

    [Fact]
    public async Task A_new_password_that_fails_policy_is_refused()
    {
        using var client = Client;
        var session = await LoginAndReadAsync(client, _tenant.OwnerEmail);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);

        var response = await client.PostAsJsonAsync(
            "/api/v1/account/password",
            new ChangePasswordRequest(_tenant.Password, "password"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCodeAsync(response)).Should().Be("identity.password_policy");
    }

    [Fact]
    public async Task Sessions_can_be_listed_and_revoked_individually()
    {
        using var client = Client;
        var session = await LoginAndReadAsync(client, _tenant.OwnerEmail);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);

        var sessions = await client.GetFromJsonAsync<List<SessionSummary>>("/api/v1/account/sessions");
        sessions.Should().NotBeNull();
        sessions!.Should().Contain(s => s.SessionId == session.User.SessionId);

        var revoked = await client.DeleteAsync($"/api/v1/account/sessions/{session.User.SessionId}");
        revoked.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var refresh = await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(session.RefreshToken));
        refresh.StatusCode.Should().Be(HttpStatusCode.BadRequest, "a revoked session cannot be refreshed");
    }

    [Fact]
    public async Task A_forgotten_password_request_always_answers_the_same_way()
    {
        using var client = Client;

        var known = await client.PostAsJsonAsync(
            "/api/v1/auth/forgot-password",
            new ForgotPasswordRequest(_tenant.OrganizationCode, _tenant.OwnerEmail));

        var unknown = await client.PostAsJsonAsync(
            "/api/v1/auth/forgot-password",
            new ForgotPasswordRequest(_tenant.OrganizationCode, "nobody@nowhere.example"));

        known.StatusCode.Should().Be(HttpStatusCode.NoContent);
        unknown.StatusCode.Should().Be(
            HttpStatusCode.NoContent,
            "a different answer would turn this into an account-enumeration tool");
    }

    [Fact]
    public async Task A_reset_token_that_was_never_issued_is_refused()
    {
        using var client = Client;

        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/reset-password",
            new ResetPasswordRequest(_tenant.OrganizationCode, _tenant.OwnerEmail, "made-up-token", "Replacem3nt!2026"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCodeAsync(response)).Should().Be("identity.token_invalid");
    }

    // ---------------------------------------------------------------- helpers

    private Task<HttpResponseMessage> LoginAsync(
        HttpClient client,
        string email,
        string? password = null,
        string? organizationCode = null) =>
        client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new LoginRequest(
                organizationCode ?? _tenant.OrganizationCode,
                email,
                password ?? _tenant.Password));

    private async Task<AuthenticationResponse> LoginAndReadAsync(
        HttpClient client,
        string email,
        string? organizationCode = null)
    {
        var response = await LoginAsync(client, email, organizationCode: organizationCode);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        return (await response.Content.ReadFromJsonAsync<AuthenticationResponse>())!;
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}
