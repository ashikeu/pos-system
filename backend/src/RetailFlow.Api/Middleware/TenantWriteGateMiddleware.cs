using RetailFlow.Api.Configuration;
using RetailFlow.Application.Platform.Abstractions;
using RetailFlow.Application.Tenancy.Abstractions;
using RetailFlow.Application.Tenancy.UseCases;

namespace RetailFlow.Api.Middleware;

/// <summary>
/// Marks an endpoint as part of the surface a suspended or lapsed tenant keeps: reading its data
/// and settling the account (FR-TEN-06).
/// </summary>
/// <remarks>
/// Needed only on endpoints that write. Reads are never gated, so a GET does not carry this.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class AllowReadOnlyTenantAttribute : Attribute;

/// <summary>
/// Turns a suspended or lapsed tenant into a read-only one (FR-TEN-06): reads pass, writes are
/// refused with <c>tenancy.organization_read_only</c>, and the renewal and account endpoints stay
/// open so the shop can fix the situation.
/// </summary>
/// <remarks>
/// <para>
/// This is a gate rather than a sign-in refusal because a shop whose payment is late still needs
/// its sales history, its stock figures and a way to pay (ADR-0030). It sits after the
/// authentication context middleware, so the tenant it checks is the one the token established and
/// not something a header can influence.
/// </para>
/// <para>
/// The status answer is cached for seconds, so a suspension takes effect almost immediately
/// without putting a query on the path of every request.
/// </para>
/// </remarks>
public sealed class TenantWriteGateMiddleware
{
    private static readonly string[] WriteMethods = ["POST", "PUT", "PATCH", "DELETE"];

    private readonly RequestDelegate _next;

    public TenantWriteGateMiddleware(RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);
        _next = next;
    }

    public async Task InvokeAsync(
        HttpContext context,
        ITenantContext tenant,
        ITenantStatusProvider status)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(status);

        if (!tenant.HasTenant || !IsWrite(context.Request.Method))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var endpoint = context.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<AllowReadOnlyTenantAttribute>() is not null)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var snapshot = await status
            .GetAsync(tenant.OrganizationId, context.RequestAborted)
            .ConfigureAwait(false);

        if (snapshot is null || snapshot.AllowsWrites)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var error = snapshot.IsClosed
            ? TenancyErrors.TenantClosed()
            : TenancyErrors.TenantReadOnly(snapshot.Reason ?? "This organization is read-only.");

        var problem = ApiProblemDetails.From(error, context);

        context.Response.StatusCode = problem.Status ?? StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/problem+json";

        await context.Response.WriteAsJsonAsync(problem, context.RequestAborted).ConfigureAwait(false);
    }

    private static bool IsWrite(string method) =>
        WriteMethods.Contains(method, StringComparer.OrdinalIgnoreCase);
}
