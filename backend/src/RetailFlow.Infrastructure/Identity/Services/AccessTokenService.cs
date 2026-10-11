using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using RetailFlow.Application.Identity.Abstractions;
using RetailFlow.Application.Platform.Abstractions;

namespace RetailFlow.Infrastructure.Identity.Services;

/// <summary>Claim names used in access tokens. Short on purpose: a POS sends them on every call.</summary>
public static class RetailFlowClaims
{
    public const string OrganizationId = "org";
    public const string BranchId = "branch";
    public const string TerminalId = "terminal";
    public const string DeviceId = "device";
    public const string SessionId = "sid";
    public const string SecurityStamp = "stamp";
    public const string PermissionsVersion = "pv";
    public const string Role = "role";
}

/// <summary>
/// Issues signed JWT access tokens (ADR-0027). Permissions are deliberately <em>not</em> inlined:
/// a cashier can hold sixty codes, and baking them into a token means a revoked permission keeps
/// working until the token expires. The token carries identity and scope; permissions are resolved
/// server-side on every request.
/// </summary>
internal sealed class JwtAccessTokenService : IAccessTokenService
{
    private readonly AuthenticationOptions _options;
    private readonly IClock _clock;
    private readonly SigningCredentials _credentials;

    public JwtAccessTokenService(AuthenticationOptions options, IClock clock, ILogger<JwtAccessTokenService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _clock = clock;

        if (string.IsNullOrWhiteSpace(options.SigningKey) || options.SigningKey.Length < 32)
        {
            throw new InvalidOperationException(
                "Jwt:SigningKey must be at least 32 characters. Set it in appsettings (ADR-0023).");
        }

        if (!string.Equals(options.Algorithm, SecurityAlgorithms.HmacSha256, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(options.Algorithm, "HS256", StringComparison.OrdinalIgnoreCase))
        {
            // RS256 needs a key pair rather than a shared secret; it is wired in the deployment
            // phase together with key management, and failing here beats signing with the wrong key.
            throw new NotSupportedException(
                $"Jwt:Algorithm '{options.Algorithm}' is not supported yet. Only HS256 is implemented.");
        }

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey));
        _credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
    }

    public AccessToken Issue(AccessTokenRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var now = _clock.UtcNow;
        var expires = now.AddMinutes(_options.AccessTokenMinutes);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Subject, request.UserId.ToString()),
            new(RetailFlowClaims.OrganizationId, request.OrganizationId.ToString()),
            new(RetailFlowClaims.SessionId, request.SessionId.ToString()),
            new(RetailFlowClaims.SecurityStamp, request.SecurityStamp),
            new(RetailFlowClaims.PermissionsVersion, request.PermissionsVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        };

        if (request.BranchId is { } branchId)
        {
            claims.Add(new Claim(RetailFlowClaims.BranchId, branchId.ToString()));
        }

        if (request.TerminalId is { } terminalId)
        {
            claims.Add(new Claim(RetailFlowClaims.TerminalId, terminalId.ToString()));
        }

        if (request.DeviceId is { } deviceId)
        {
            claims.Add(new Claim(RetailFlowClaims.DeviceId, deviceId.ToString()));
        }

        if (request.Roles is { Count: > 0 })
        {
            claims.AddRange(request.Roles.Select(role => new Claim(RetailFlowClaims.Role, role)));
        }

        var token = new JwtSecurityToken(
            _options.Issuer,
            _options.Audience,
            claims,
            now.UtcDateTime,
            expires.UtcDateTime,
            _credentials);

        var value = new JwtSecurityTokenHandler().WriteToken(token);

        return new AccessToken(value, expires, (int)(expires - now).TotalSeconds);
    }
}

/// <summary>Registered claim names used here, spelled out to avoid a dependency on a constants class.</summary>
internal static class JwtRegisteredClaimNames
{
    public const string Subject = "sub";
    public const string Jti = "jti";
}
