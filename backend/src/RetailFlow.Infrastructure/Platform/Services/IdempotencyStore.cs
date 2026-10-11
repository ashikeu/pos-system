using Microsoft.EntityFrameworkCore;
using RetailFlow.Application.Platform.Abstractions;
using RetailFlow.Infrastructure.Platform.Persistence;
using RetailFlow.Infrastructure.Platform.Persistence.Records;

namespace RetailFlow.Infrastructure.Platform.Services;

/// <summary>
/// Database-backed idempotency store (ADR-0010). The uniqueness guarantee is the unique index on
/// <c>(OrganizationId, Key)</c>, not this code: if two terminals replay the same key at the same
/// instant, one insert wins and the other is told the operation is already in flight.
/// </summary>
internal sealed class IdempotencyStore : IIdempotencyStore
{
    /// <summary>
    /// How long a reservation may stay in progress before it is assumed abandoned. A crashed
    /// process leaves its transaction rolled back, so taking the key over is safe — and necessary,
    /// otherwise a terminal could never retry that operation again.
    /// </summary>
    private static readonly TimeSpan StaleReservationWindow = TimeSpan.FromMinutes(5);

    private readonly RetailFlowDbContext _context;
    private readonly IClock _clock;
    private readonly IGuidGenerator _guids;

    public IdempotencyStore(RetailFlowDbContext context, IClock clock, IGuidGenerator guids)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(guids);

        _context = context;
        _clock = clock;
        _guids = guids;
    }

    public async Task<IdempotencyLookup> FindAsync(
        Guid organizationId,
        string key,
        string endpoint,
        string requestHash,
        CancellationToken cancellationToken = default)
    {
        var record = await _context.IdempotencyRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(
                r => r.OrganizationId == organizationId && r.Key == key,
                cancellationToken)
            .ConfigureAwait(false);

        return Evaluate(record, endpoint, requestHash, _clock.UtcNow);
    }

    public async Task<IdempotencyLookup> BeginAsync(
        Guid organizationId,
        string key,
        string endpoint,
        string requestHash,
        TimeSpan retention,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;

        var existing = await _context.IdempotencyRecords
            .FirstOrDefaultAsync(
                r => r.OrganizationId == organizationId && r.Key == key,
                cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            var evaluated = Evaluate(existing, endpoint, requestHash, now);

            if (evaluated.Outcome == IdempotencyOutcome.New)
            {
                // A stale reservation from a process that died mid-flight: take it over.
                existing.TakeOver(now, requestHash);
            }

            return evaluated;
        }

        var record = IdempotencyRecord.Begin(
            _guids.NewId(now),
            organizationId,
            key,
            endpoint,
            requestHash,
            now,
            retention);

        await _context.IdempotencyRecords.AddAsync(record, cancellationToken).ConfigureAwait(false);

        // Flushed immediately so a competing caller hits the unique index now rather than at the
        // end of a long business transaction.
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return IdempotencyLookup.New;
    }

    public async Task CompleteAsync(
        Guid organizationId,
        string key,
        int httpStatusCode,
        string? responseJson,
        Guid? resourceId,
        CancellationToken cancellationToken = default)
    {
        var record = await _context.IdempotencyRecords
            .FirstOrDefaultAsync(
                r => r.OrganizationId == organizationId && r.Key == key,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Idempotency key '{key}' was not reserved before completion; this is a pipeline defect.");

        record.Complete(_clock.UtcNow, httpStatusCode, responseJson, resourceId);
    }

    private static IdempotencyLookup Evaluate(
        IdempotencyRecord? record,
        string endpoint,
        string requestHash,
        DateTimeOffset now)
    {
        if (record is null)
        {
            return IdempotencyLookup.New;
        }

        // The same key used for a different operation or a different body is a client defect, and
        // replaying an unrelated response would be worse than failing.
        if (!string.Equals(record.Endpoint, endpoint, StringComparison.Ordinal)
            || !string.Equals(record.RequestHash, requestHash, StringComparison.Ordinal))
        {
            return new IdempotencyLookup(IdempotencyOutcome.Mismatch);
        }

        if (record.Status == IdempotencyRecordStatus.Completed)
        {
            return new IdempotencyLookup(
                IdempotencyOutcome.Completed,
                record.HttpStatusCode,
                record.ResponseJson,
                record.ResourceId);
        }

        return record.IsStaleReservation(now, StaleReservationWindow)
            ? IdempotencyLookup.New
            : new IdempotencyLookup(IdempotencyOutcome.InProgress);
    }
}
