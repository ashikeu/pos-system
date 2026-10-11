using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using RetailFlow.Application.Platform.Abstractions;

namespace RetailFlow.Infrastructure.Platform.Persistence.SqlServer;

/// <summary>
/// Takes write locks on specific rows before they are read and updated (ADR-0008). Optimistic
/// concurrency is not enough for "two terminals sell the last unit at the same moment": both would
/// read the same quantity, both would pass validation, and one would then fail at commit — after
/// the customer has been told the sale went through. A row lock makes the second terminal wait and
/// then see the truth.
/// </summary>
/// <remarks>
/// Locks are always taken in ascending key order so two transactions cannot deadlock by acquiring
/// the same pair in opposite orders (ADR-0008). The PostgreSQL and MySQL equivalent is
/// <c>SELECT ... FOR UPDATE</c>.
/// </remarks>
internal sealed class SqlServerRowLocker : IRowLocker
{
    private readonly RetailFlowDbContext _context;

    public SqlServerRowLocker(RetailFlowDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    public async Task LockForUpdateAsync<TEntity>(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(ids);

        if (ids.Count == 0)
        {
            return;
        }

        if (_context.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "Row locks must be taken inside a transaction, otherwise they are released immediately.");
        }

        var entityType = _context.Model.FindEntityType(typeof(TEntity))
            ?? throw new InvalidOperationException($"{typeof(TEntity).Name} is not part of the model.");

        var tableName = entityType.GetTableName()
            ?? throw new InvalidOperationException($"{typeof(TEntity).Name} is not mapped to a table.");

        var keyProperty = entityType.FindPrimaryKey()?.Properties.SingleOrDefault()
            ?? throw new InvalidOperationException(
                $"{typeof(TEntity).Name} must have a single-column primary key to be locked by id.");

        var keyColumn = keyProperty.GetColumnName();
        var schema = entityType.GetSchema();
        var qualifiedTable = schema is null ? $"[{tableName}]" : $"[{schema}].[{tableName}]";

        var ordered = ids.Distinct().OrderBy(id => id).ToArray();
        var parameters = new List<SqlParameter>(ordered.Length);
        var placeholders = new List<string>(ordered.Length);

        for (var i = 0; i < ordered.Length; i++)
        {
            var name = "@id" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            placeholders.Add(name);
            parameters.Add(new SqlParameter(name, ordered[i]));
        }

        // UPDLOCK takes the update lock the subsequent write needs; HOLDLOCK keeps it until the
        // transaction ends; ROWLOCK stops SQL Server escalating to a page or table lock, which
        // would block unrelated products at the same terminal.
        var sql =
            $"SELECT [{keyColumn}] FROM {qualifiedTable} WITH (UPDLOCK, HOLDLOCK, ROWLOCK) " +
            $"WHERE [{keyColumn}] IN ({string.Join(", ", placeholders)}) " +
            $"ORDER BY [{keyColumn}]";

        await _context.Database
            .ExecuteSqlRawAsync(sql, parameters, cancellationToken)
            .ConfigureAwait(false);
    }
}
