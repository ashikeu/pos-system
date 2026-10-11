using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using RetailFlow.Api.Authorization;
using RetailFlow.Application;
using RetailFlow.Application.Identity.Abstractions;
using RetailFlow.Application.Identity.Contracts;
using RetailFlow.Application.Identity.UseCases;
using RetailFlow.Application.Platform.Abstractions;
using RetailFlow.Domain.Platform;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace RetailFlow.Api.Configuration;

/// <summary>Named rate-limit policies (FR-SEC-09).</summary>
public static class RateLimitPolicies
{
    /// <summary>Applied to <c>/auth/*</c>: tight, because these endpoints are unauthenticated.</summary>
    public const string Authentication = "auth";

    /// <summary>
    /// The broad budget, applied globally to every request rather than as an endpoint policy, so
    /// an endpoint-level policy tightens it instead of replacing it.
    /// </summary>
    public const string General = "general";
}

/// <summary>Authentication, authorization, rate limiting and the identity use-case registrations.</summary>
public static class AuthenticationSetup
{
    public static IServiceCollection AddRetailFlowAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = Infrastructure.DependencyInjection.BuildAuthenticationOptions(configuration);

        if (string.IsNullOrWhiteSpace(options.SigningKey) || options.SigningKey.Length < 32)
        {
            throw new InvalidOperationException(
                "Jwt:SigningKey must be at least 32 characters. Set it in appsettings (ADR-0023).");
        }

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(jwt =>
            {
                jwt.MapInboundClaims = false;
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = options.Issuer,
                    ValidateAudience = true,
                    ValidAudience = options.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey)),
                    ValidateLifetime = true,

                    // A few seconds of tolerance for a till whose clock drifts; not minutes, or an
                    // expired token would keep working past its lifetime (ADR-0007).
                    ClockSkew = TimeSpan.FromSeconds(options.ClockSkewSeconds),
                    NameClaimType = "name",
                    RoleClaimType = Infrastructure.Identity.Services.RetailFlowClaims.Role,
                };

                // Authentication failures answer in the same ProblemDetails shape as everything
                // else, so a client has one error format to handle (ADR-0014).
                jwt.Events = new JwtBearerEvents
                {
                    OnChallenge = async context =>
                    {
                        context.HandleResponse();

                        var error = Error.Unauthenticated();
                        var problem = ApiProblemDetails.From(error, context.HttpContext);

                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        context.Response.ContentType = "application/problem+json";

                        await context.Response
                            .WriteAsJsonAsync(problem, context.HttpContext.RequestAborted)
                            .ConfigureAwait(false);
                    },
                };
            });

        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
        services.AddAuthorization();

        return services;
    }

    /// <summary>
    /// Registers the identity use cases with the standard pipeline. Each one is listed explicitly
    /// so the decorators around it are visible in source (ADR-0015).
    /// </summary>
    public static IServiceCollection AddIdentityUseCases(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<AuthenticationIssuer>();

        services.AddUseCase<LoginRequest, AuthenticationResponse, LoginHandler>();
        services.AddUseCase<RefreshRequest, AuthenticationResponse, RefreshTokenHandler>();
        services.AddUseCase<LogoutRequest, bool, LogoutHandler>();
        services.AddUseCase<LogoutAllRequest, int, LogoutAllHandler>();
        services.AddUseCase<ChangePasswordRequest, bool, ChangePasswordHandler>();
        services.AddUseCase<ForgotPasswordRequest, bool, ForgotPasswordHandler>();
        services.AddUseCase<ResetPasswordRequest, bool, ResetPasswordHandler>();
        services.AddUseCase<VerifyEmailRequest, bool, VerifyEmailHandler>();
        services.AddUseCase<BeginMfaEnrolmentRequest, MfaEnrolmentResponse, BeginMfaEnrolmentHandler>();
        services.AddUseCase<ConfirmMfaRequest, bool, ConfirmMfaHandler>();
        services.AddUseCase<DisableMfaRequest, bool, DisableMfaHandler>();
        services.AddUseCase<ListSessionsRequest, IReadOnlyList<SessionSummary>, ListSessionsHandler>();
        services.AddUseCase<RevokeSessionRequest, bool, RevokeSessionHandler>();
        services.AddUseCase<LoginHistoryRequest, IReadOnlyList<LoginHistoryEntry>, LoginHistoryHandler>();
        services.AddUseCase<RegisterDeviceRequest, RegisterDeviceResponse, RegisterDeviceHandler>();

        return services;
    }

    /// <summary>
    /// Rate limits (FR-SEC-09). Partitioned by source address, because an unauthenticated endpoint
    /// has nothing else to key on, and a shop behind one NAT address still gets a workable budget.
    /// </summary>
    public static IServiceCollection AddRetailFlowRateLimiting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var authPerMinute = configuration.GetValue<int?>("Api:RateLimit:AuthRequestsPerMinute") ?? 10;
        var generalPerMinute = configuration.GetValue<int?>("Api:RateLimit:GeneralRequestsPerMinute") ?? 300;

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // The general budget is global rather than an endpoint policy: an endpoint can carry
            // only one rate-limiting policy, and the last one applied wins. Making the broad limit
            // global means a per-endpoint policy tightens it instead of replacing it.
            limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    PartitionKey(context),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = generalPerMinute,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    }));

            limiter.AddPolicy(RateLimitPolicies.Authentication, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    PartitionKey(context),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = authPerMinute,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    }));

            limiter.OnRejected = async (context, cancellationToken) =>
            {
                var problem = ApiProblemDetails.From(
                    new Error(
                        "platform.rate_limited",
                        "Too many requests. Slow down and try again.",
                        ErrorType.RateLimited),
                    context.HttpContext);

                context.HttpContext.Response.ContentType = "application/problem+json";

                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)retryAfter.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                await context.HttpContext.Response
                    .WriteAsJsonAsync(problem, cancellationToken)
                    .ConfigureAwait(false);
            };
        });

        return services;
    }

    /// <summary>Adds the bearer-token scheme to the OpenAPI document so Swagger can authorise.</summary>
    public static void AddBearerSecurity(SwaggerGenOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
        {
            Name = "Authorization",
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            In = ParameterLocation.Header,
            Description = "Paste the access token returned by /api/v1/auth/login.",
        });

        options.AddSecurityRequirement(new OpenApiSecurityRequirement
        {
            [new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" },
            }] = Array.Empty<string>(),
        });
    }

    private static string PartitionKey(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
