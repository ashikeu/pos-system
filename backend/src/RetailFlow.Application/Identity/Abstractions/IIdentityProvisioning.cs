using RetailFlow.Domain.Platform;

namespace RetailFlow.Application.Identity.Abstractions;

/// <summary>The first account of a new organization.</summary>
/// <param name="Email">Sign-in address.</param>
/// <param name="FullName">Display name.</param>
/// <param name="Password">Initial password.</param>
/// <param name="MustChangePassword">
/// Whether the first sign-in has to replace the password. True for a real tenant, because whoever
/// provisioned it chose that password; false only for the development seed account.
/// </param>
/// <param name="EmailConfirmed">
/// Whether the address counts as verified. False for a real tenant, which goes through the
/// verification email like any other account.
/// </param>
/// <param name="UserId">
/// Pins the account's identifier. Provisioning leaves this null and lets the generator choose;
/// only the development seeder sets it, so a developer can hard-code the demo owner's id.
/// </param>
public sealed record OwnerAccountRequest(
    string Email,
    string FullName,
    string Password,
    bool MustChangePassword = true,
    bool EmailConfirmed = false,
    Guid? UserId = null);

/// <summary>
/// The identity work a new organization needs: its copy of the role templates and its first user.
/// Exposed as an Application-layer contract because tenancy provisioning drives it, and a module
/// may only reach another module through its contracts, never its entities.
/// </summary>
public interface IIdentityProvisioning
{
    /// <summary>
    /// Copies the system role templates into an organization, adding only what is missing, and
    /// returns role ids by name (ADR-0013).
    /// </summary>
    Task<IReadOnlyDictionary<string, Guid>> EnsureSystemRolesAsync(
        Guid organizationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the organization's owner account, grants it the given branch as default and assigns
    /// the Owner role. Fails when the address is already used in that organization, or when the
    /// password does not meet the policy.
    /// </summary>
    Task<Result<Guid>> CreateOwnerAsync(
        Guid organizationId,
        Guid branchId,
        OwnerAccountRequest owner,
        CancellationToken cancellationToken = default);

    /// <summary>Active user accounts in an organization, for the plan limit on users (FR-TEN-05).</summary>
    Task<int> CountActiveUsersAsync(Guid organizationId, CancellationToken cancellationToken = default);
}
