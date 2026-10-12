using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using RetailFlow.Application.Platform.Abstractions;
using RetailFlow.Domain.Identity;
using RetailFlow.Domain.Platform;
using RetailFlow.Domain.Tenancy;
using RetailFlow.Infrastructure.Platform.Persistence.Records;
using RetailFlow.Infrastructure.Platform.Persistence.SqlServer;

namespace RetailFlow.Infrastructure.Platform.Persistence;

/// <summary>
/// The single database context. Tenant isolation, audit stamping, change sequencing and outbox
/// draining are enforced here and in the save interceptor, so no module can bypass them
/// (ADR-0004, ADR-0011, ADR-0018).
/// </summary>
public sealed class RetailFlowDbContext : DbContext
{
    private readonly ITenantContext _tenantContext;

    public RetailFlowDbContext(DbContextOptions<RetailFlowDbContext> options, ITenantContext tenantContext)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(tenantContext);
        _tenantContext = tenantContext;
    }

    public DbSet<Organization> Organizations => Set<Organization>();

    public DbSet<Branch> Branches => Set<Branch>();

    public DbSet<Warehouse> Warehouses => Set<Warehouse>();

    public DbSet<PosTerminal> PosTerminals => Set<PosTerminal>();

    public DbSet<OrganizationSettings> OrganizationSettings => Set<OrganizationSettings>();

    public DbSet<BranchSettings> BranchSettings => Set<BranchSettings>();

    public DbSet<SubscriptionPlan> SubscriptionPlans => Set<SubscriptionPlan>();

    public DbSet<PlanFeature> PlanFeatures => Set<PlanFeature>();

    public DbSet<OrganizationSubscription> OrganizationSubscriptions => Set<OrganizationSubscription>();

    public DbSet<User> Users => Set<User>();

    public DbSet<Role> Roles => Set<Role>();

    public DbSet<Permission> Permissions => Set<Permission>();

    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();

    public DbSet<UserRole> UserRoles => Set<UserRole>();

    public DbSet<UserBranch> UserBranches => Set<UserBranch>();

    public DbSet<UserBranchPermission> UserBranchPermissions => Set<UserBranchPermission>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<UserSession> UserSessions => Set<UserSession>();

    public DbSet<LoginAttempt> LoginAttempts => Set<LoginAttempt>();

    public DbSet<DeviceRegistration> DeviceRegistrations => Set<DeviceRegistration>();

    public DbSet<MfaSecret> MfaSecrets => Set<MfaSecret>();

    public DbSet<UserToken> UserTokens => Set<UserToken>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    public DbSet<AuditLogEntry> AuditLogs => Set<AuditLogEntry>();

    public DbSet<TenantChangeSequence> TenantChangeSequences => Set<TenantChangeSequence>();

    /// <summary>
    /// The organization every tenant-scoped query is filtered by. <see cref="Guid.Empty"/> when no
    /// tenant context exists, which matches nothing — the filter fails closed, so a missing tenant
    /// returns no data instead of everyone's data (ADR-0004).
    /// </summary>
    public Guid CurrentOrganizationId => _tenantContext.OrganizationIdOrNull ?? Guid.Empty;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(RetailFlowDbContext).Assembly);

        ApplyTenantQueryFilters(modelBuilder);

        if (Database.IsSqlServer())
        {
            SqlServerModelTuning.Apply(modelBuilder);
        }

        base.OnModelCreating(modelBuilder);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        // Money and quantity precision in one place (ADR-0006): a column that silently rounds is a
        // financial bug waiting to happen, so nothing relies on the provider default.
        configurationBuilder.Properties<decimal>().HavePrecision(19, 4);
        configurationBuilder.Properties<string>().HaveMaxLength(256);

        base.ConfigureConventions(configurationBuilder);
    }

    /// <summary>
    /// Adds the organization filter to every entity that implements <see cref="ITenantOwned"/>, and
    /// restricts <see cref="Organization"/> itself to the current tenant. Cross-tenant access is
    /// possible only through an explicit, audited <c>IgnoreQueryFilters</c> call.
    /// </summary>
    /// <remarks>
    /// The filters reference the context property <see cref="CurrentOrganizationId"/> rather than a
    /// captured value, so EF re-evaluates it per query execution instead of baking the tenant of
    /// the first request into the cached model.
    /// </remarks>
    private void ApplyTenantQueryFilters(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Organization>().HasQueryFilter(o => o.Id == CurrentOrganizationId);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var clrType = entityType.ClrType;

            if (!typeof(ITenantOwned).IsAssignableFrom(clrType))
            {
                continue;
            }

            var parameter = Expression.Parameter(clrType, "e");
            var contextProperty = Expression.Property(
                Expression.Constant(this),
                nameof(CurrentOrganizationId));
            var body = Expression.Equal(
                Expression.Property(parameter, nameof(ITenantOwned.OrganizationId)),
                contextProperty);

            modelBuilder.Entity(clrType).HasQueryFilter(Expression.Lambda(body, parameter));
        }
    }
}
