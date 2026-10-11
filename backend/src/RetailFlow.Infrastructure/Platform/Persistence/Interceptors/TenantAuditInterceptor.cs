using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RetailFlow.Application.Platform.Abstractions;
using RetailFlow.Application.Platform.UseCases;
using RetailFlow.Domain.Platform;
using RetailFlow.Infrastructure.Platform.Persistence.Records;
using RetailFlow.Infrastructure.Platform.Persistence.SqlServer;

namespace RetailFlow.Infrastructure.Platform.Persistence.Interceptors;

/// <summary>
/// The enforcement point for tenancy, auditing, sync sequencing and event publication (ADR-0004,
/// ADR-0011, ADR-0018). Doing this in the save pipeline rather than in each handler means a new
/// module cannot forget it, and a hand-written query cannot route around it.
/// </summary>
public sealed class TenantAuditInterceptor : SaveChangesInterceptor
{
    private readonly ITenantContext _tenant;
    private readonly ICurrentUser _currentUser;
    private readonly ICorrelationContext _correlation;
    private readonly IClock _clock;
    private readonly IGuidGenerator _guids;
    private readonly IChangeSequenceAllocator _sequences;

    public TenantAuditInterceptor(
        ITenantContext tenant,
        ICurrentUser currentUser,
        ICorrelationContext correlation,
        IClock clock,
        IGuidGenerator guids,
        IChangeSequenceAllocator sequences)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(currentUser);
        ArgumentNullException.ThrowIfNull(correlation);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(guids);
        ArgumentNullException.ThrowIfNull(sequences);

        _tenant = tenant;
        _currentUser = currentUser;
        _correlation = correlation;
        _clock = clock;
        _guids = guids;
        _sequences = sequences;
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        if (eventData.Context is not null)
        {
            await ApplyAsync(eventData.Context, cancellationToken).ConfigureAwait(false);
        }

        return await base.SavingChangesAsync(eventData, result, cancellationToken).ConfigureAwait(false);
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        if (eventData.Context is not null)
        {
            // Synchronous saves cannot allocate sequences without blocking, and blocking on a
            // database call inside the save pipeline is how thread-pool starvation starts. Every
            // write path in this system is async, so this is a defect rather than a scenario.
            throw new NotSupportedException(
                "Use SaveChangesAsync: synchronous saves cannot stamp tenancy and sync sequences safely.");
        }

        return base.SavingChanges(eventData, result);
    }

    private async Task ApplyAsync(DbContext context, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var userId = _currentUser.UserId;
        var ambientTenant = _tenant.OrganizationIdOrNull;

        context.ChangeTracker.DetectChanges();

        StampTenancyAndAudit(context, now, userId, ambientTenant);
        DrainDomainEvents(context, now);
        await AssignChangeSequencesAsync(context, cancellationToken).ConfigureAwait(false);
    }

    private static void StampTenancyAndAudit(DbContext context, DateTimeOffset now, Guid? userId, Guid? ambientTenant)
    {
        foreach (var entry in context.ChangeTracker.Entries().ToArray())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    if (entry.Entity is ITenantOwned addedTenantOwned)
                    {
                        StampTenant(entry, addedTenantOwned, ambientTenant);
                    }

                    (entry.Entity as IAuditable)?.MarkCreated(now, userId);
                    break;

                case EntityState.Modified:
                    if (entry.Entity is ITenantOwned modifiedTenantOwned)
                    {
                        GuardTenant(entry, modifiedTenantOwned, ambientTenant);
                    }

                    (entry.Entity as IAuditable)?.MarkUpdated(now, userId);
                    break;

                case EntityState.Deleted:
                    if (entry.Entity is ITenantOwned deletedTenantOwned)
                    {
                        GuardTenant(entry, deletedTenantOwned, ambientTenant);
                    }

                    // Records the business wants to recall are withdrawn, not removed, so history
                    // and reports stay intact (NFR-09).
                    if (entry.Entity is ISoftDeletable softDeletable)
                    {
                        entry.State = EntityState.Modified;
                        softDeletable.MarkDeleted(now, userId);
                    }

                    break;

                case EntityState.Detached:
                case EntityState.Unchanged:
                default:
                    break;
            }
        }
    }

    private static void StampTenant(EntityEntry entry, ITenantOwned entity, Guid? ambientTenant)
    {
        if (entity.OrganizationId == Guid.Empty)
        {
            if (ambientTenant is null)
            {
                throw new MissingTenantContextException(
                    $"{entry.Metadata.ClrType.Name} cannot be inserted without an organization context.");
            }

            entity.AssignOrganization(ambientTenant.Value);
            return;
        }

        GuardTenant(entry, entity, ambientTenant);
    }

    private static void GuardTenant(EntityEntry entry, ITenantOwned entity, Guid? ambientTenant)
    {
        if (ambientTenant is null)
        {
            // A background job that writes tenant data must set the tenant it is working for; no
            // context means the write is unattributable, which is never acceptable.
            throw new MissingTenantContextException(
                $"{entry.Metadata.ClrType.Name} was written without an organization context.");
        }

        if (entity.OrganizationId != ambientTenant.Value)
        {
            throw new TenantMismatchException(
                entity.OrganizationId,
                ambientTenant.Value,
                entry.Metadata.ClrType.Name,
                entry.Entity is Entity typed ? typed.Id : Guid.Empty);
        }
    }

    /// <summary>
    /// Moves the events raised by aggregates into the outbox table in this same transaction, so a
    /// committed change always has its event and a rolled-back change never does (ADR-0011).
    /// </summary>
    private void DrainDomainEvents(DbContext context, DateTimeOffset now)
    {
        var roots = context.ChangeTracker.Entries<AggregateRoot>()
            .Where(e => e.Entity.DomainEvents.Count > 0)
            .Select(e => e.Entity)
            .ToArray();

        foreach (var root in roots)
        {
            var organizationId = root is ITenantOwned owned ? owned.OrganizationId : (Guid?)null;

            foreach (var domainEvent in root.DomainEvents)
            {
                context.Add(OutboxMessage.Create(
                    domainEvent.EventId == Guid.Empty ? _guids.NewId() : domainEvent.EventId,
                    organizationId,
                    domainEvent.EventType,
                    PlatformJson.Serialize((object)domainEvent),
                    now,
                    _correlation.CorrelationId));
            }

            root.ClearDomainEvents();
        }
    }

    /// <summary>
    /// Stamps every syncable row with the next value of its tenant cursor. Values are allocated in
    /// one block per tenant, so a 50-line sale costs one allocation, not fifty (ADR-0018).
    /// </summary>
    private async Task AssignChangeSequencesAsync(DbContext context, CancellationToken cancellationToken)
    {
        var pending = context.ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified)
            .Where(e => e.Entity is IHasChangeSequence)
            .Select(e => e.Entity)
            .Cast<IHasChangeSequence>()
            .ToArray();

        if (pending.Length == 0)
        {
            return;
        }

        foreach (var group in pending.GroupBy(ResolveOrganizationId))
        {
            var items = group.ToArray();
            var first = await _sequences
                .AllocateAsync(context, group.Key, items.Length, cancellationToken)
                .ConfigureAwait(false);

            for (var i = 0; i < items.Length; i++)
            {
                items[i].AssignChangeSequence(first + i);
            }
        }
    }

    private Guid ResolveOrganizationId(IHasChangeSequence entity) => entity switch
    {
        ITenantOwned owned => owned.OrganizationId,

        // An organization's own row is sequenced under itself, so a client pulling its tenant
        // record sees the same cursor as everything else it owns.
        Entity root when root.GetType() == typeof(Domain.Tenancy.Organization) => root.Id,

        _ => _tenant.OrganizationIdOrNull
            ?? throw new MissingTenantContextException(
                $"{entity.GetType().Name} needs an organization context to be sequenced."),
    };
}
