using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using RetailFlow.Infrastructure.Platform.Persistence.Records;

namespace RetailFlow.Infrastructure.Platform.Persistence.SqlServer;

/// <summary>
/// Hands out the per-tenant sync cursor values (ADR-0018). Every insert and update of a syncable
/// row takes the next value, so an offline client can ask for "everything after sequence N" and be
/// certain it missed nothing.
/// </summary>
public interface IChangeSequenceAllocator
{
    /// <summary>
    /// Reserves <paramref name="count"/> consecutive values for the tenant and returns the first.
    /// Must run inside the caller's transaction so the reservation shares its fate.
    /// </summary>
    Task<long> AllocateAsync(
        DbContext context,
        Guid organizationId,
        int count,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// SQL Server implementation: one statement updates the counter and returns the new value under a
/// row lock, which is both atomic and a single round trip.
/// </summary>
/// <remarks>
/// This is deliberately provider-specific and isolated here. The PostgreSQL and MySQL equivalents
/// are a <c>SELECT ... FOR UPDATE</c> followed by an update, or <c>UPDATE ... RETURNING</c>
/// (ADR-0003). A database sequence was rejected because its values would not restart per tenant and
/// the cursor's meaning has to be identical on every provider.
/// </remarks>
internal sealed class SqlServerChangeSequenceAllocator : IChangeSequenceAllocator
{
    private const string UpdateSql = """
        UPDATE [TenantChangeSequence] WITH (ROWLOCK)
        SET [LastSequence] = [LastSequence] + @count
        OUTPUT inserted.[LastSequence] AS [Value]
        WHERE [OrganizationId] = @organizationId
        """;

    private const int UniqueViolation = 2627;
    private const int UniqueIndexViolation = 2601;

    public async Task<long> AllocateAsync(
        DbContext context,
        Guid organizationId,
        int count,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (count < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "Count must be at least 1.");
        }

        var last = await TryBumpAsync(context, organizationId, count, cancellationToken).ConfigureAwait(false);

        if (last is null)
        {
            // First write for this tenant: create the counter, then bump it. A concurrent creator
            // may win the insert, in which case the retry below simply uses their row.
            await EnsureRowAsync(context, organizationId, cancellationToken).ConfigureAwait(false);
            last = await TryBumpAsync(context, organizationId, count, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Change sequence row for organization {organizationId} could not be created.");
        }

        return last.Value - count + 1;
    }

    private static async Task<long?> TryBumpAsync(
        DbContext context,
        Guid organizationId,
        int count,
        CancellationToken cancellationToken)
    {
        var values = await context.Database
            .SqlQueryRaw<long>(
                UpdateSql,
                new SqlParameter("@count", count),
                new SqlParameter("@organizationId", organizationId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return values.Count == 0 ? null : values[0];
    }

    private static async Task EnsureRowAsync(
        DbContext context,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        try
        {
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO [TenantChangeSequence] ([OrganizationId], [LastSequence])
                SELECT @organizationId, 0
                WHERE NOT EXISTS (
                    SELECT 1 FROM [TenantChangeSequence] WHERE [OrganizationId] = @organizationId)
                """,
                [new SqlParameter("@organizationId", organizationId)],
                cancellationToken).ConfigureAwait(false);
        }
        catch (SqlException ex) when (ex.Number is UniqueViolation or UniqueIndexViolation)
        {
            // Another transaction created the counter first; nothing to do.
        }
    }
}

/// <summary>
/// Fallback allocator for providers without the SQL above and for unit tests: reads the counter
/// through EF and relies on the surrounding transaction plus optimistic retry. Correct, but one
/// extra round trip per save.
/// </summary>
internal sealed class EntityChangeSequenceAllocator : IChangeSequenceAllocator
{
    public async Task<long> AllocateAsync(
        DbContext context,
        Guid organizationId,
        int count,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (count < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "Count must be at least 1.");
        }

        var counter = await context.Set<TenantChangeSequence>()
            .FirstOrDefaultAsync(s => s.OrganizationId == organizationId, cancellationToken)
            .ConfigureAwait(false);

        if (counter is null)
        {
            counter = TenantChangeSequence.Create(organizationId);
            await context.AddAsync(counter, cancellationToken).ConfigureAwait(false);
        }

        return counter.Reserve(count);
    }
}
