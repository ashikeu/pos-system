namespace RetailFlow.Application.Platform.Abstractions;

/// <summary>Conveniences over <see cref="IUnitOfWork"/>.</summary>
public static class UnitOfWorkExtensions
{
    /// <summary>
    /// Saves the tracked changes inside one transaction. For handlers that manage their own
    /// boundary — sign-in, for instance, which is not idempotent and so carries no key — rather
    /// than relying on the pipeline decorator.
    /// </summary>
    public static Task CommitAsync(this IUnitOfWork unitOfWork, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);

        return unitOfWork.ExecuteInTransactionAsync(
            async ct =>
            {
                await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
                return true;
            },
            cancellationToken);
    }
}
