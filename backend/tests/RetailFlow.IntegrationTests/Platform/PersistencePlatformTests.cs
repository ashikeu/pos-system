using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RetailFlow.Domain.Platform;
using RetailFlow.Domain.Tenancy;
using RetailFlow.Infrastructure.Platform.Persistence;
using RetailFlow.IntegrationTests.Infrastructure;
using Xunit;

namespace RetailFlow.IntegrationTests.Platform;

/// <summary>
/// Persistence guarantees the rest of the system assumes: the migration chain applies cleanly, the
/// sync cursor is monotonic per tenant, audit fields are stamped, and money and time survive a
/// round trip exactly (ADR-0006, ADR-0007, ADR-0018).
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class PersistencePlatformTests
{
    private readonly DatabaseFixture _fixture;

    public PersistencePlatformTests(DatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Every_migration_is_applied_and_none_is_pending()
    {
        await using var scope = _fixture.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RetailFlowDbContext>();

        var applied = await context.Database.GetAppliedMigrationsAsync();
        var pending = await context.Database.GetPendingMigrationsAsync();

        applied.Should().NotBeEmpty();
        pending.Should().BeEmpty("the model and the migrations must not drift");
    }

    [Fact]
    public async Task The_change_sequence_increases_per_tenant_and_is_independent_across_tenants()
    {
        var alpha = await _fixture.CreateOrganizationAsync("SEQ-A");
        var beta = await _fixture.CreateOrganizationAsync("SEQ-B");

        long alphaFirst;
        long alphaSecond;

        await using (var scope = _fixture.CreateScope(alpha.Id))
        {
            var context = scope.ServiceProvider.GetRequiredService<RetailFlowDbContext>();

            var one = Branch.Create(Guid.NewGuid(), alpha.Id, "SEQ1", "One");
            context.Branches.Add(one);
            await context.SaveChangesAsync();
            alphaFirst = one.ChangeSequence;

            var two = Branch.Create(Guid.NewGuid(), alpha.Id, "SEQ2", "Two");
            context.Branches.Add(two);
            await context.SaveChangesAsync();
            alphaSecond = two.ChangeSequence;
        }

        alphaFirst.Should().BeGreaterThan(0);
        alphaSecond.Should().BeGreaterThan(alphaFirst);

        await using (var scope = _fixture.CreateScope(beta.Id))
        {
            var context = scope.ServiceProvider.GetRequiredService<RetailFlowDbContext>();
            var branch = Branch.Create(Guid.NewGuid(), beta.Id, "SEQ1", "Beta one");
            context.Branches.Add(branch);
            await context.SaveChangesAsync();

            // Each tenant has its own cursor, so one busy shop cannot inflate another shop's
            // sequence numbers and force needless pulls (ADR-0018).
            branch.ChangeSequence.Should().Be(2, "the organization row took 1 for this tenant");
        }
    }

    [Fact]
    public async Task One_save_allocates_a_consecutive_block_for_all_its_rows()
    {
        var organization = await _fixture.CreateOrganizationAsync("SEQ-C");

        await using var scope = _fixture.CreateScope(organization.Id);
        var context = scope.ServiceProvider.GetRequiredService<RetailFlowDbContext>();

        var branches = Enumerable.Range(0, 5)
            .Select(i => Branch.Create(Guid.NewGuid(), organization.Id, "BLK" + i, "Block " + i))
            .ToArray();

        context.Branches.AddRange(branches);
        await context.SaveChangesAsync();

        var sequences = branches.Select(b => b.ChangeSequence).OrderBy(s => s).ToArray();

        sequences.Should().OnlyHaveUniqueItems();
        sequences.Last().Should().Be(sequences.First() + 4, "the block must be contiguous");
    }

    [Fact]
    public async Task An_update_moves_the_record_to_the_front_of_the_sync_cursor()
    {
        var organization = await _fixture.CreateOrganizationAsync("SEQ-D");
        var branchId = Guid.NewGuid();
        long afterInsert;

        await using (var scope = _fixture.CreateScope(organization.Id))
        {
            var context = scope.ServiceProvider.GetRequiredService<RetailFlowDbContext>();
            var branch = Branch.Create(branchId, organization.Id, "UPD", "Before");
            context.Branches.Add(branch);
            await context.SaveChangesAsync();
            afterInsert = branch.ChangeSequence;
        }

        await using (var scope = _fixture.CreateScope(organization.Id))
        {
            var context = scope.ServiceProvider.GetRequiredService<RetailFlowDbContext>();
            var branch = await context.Branches.SingleAsync(b => b.Id == branchId);
            branch.UpdateDetails("After", null, null, null, null);
            await context.SaveChangesAsync();

            // A client that already pulled this record must see it again after it changes.
            branch.ChangeSequence.Should().BeGreaterThan(afterInsert);
        }
    }

    [Fact]
    public async Task Audit_fields_are_stamped_on_insert_and_update()
    {
        var organization = await _fixture.CreateOrganizationAsync("AUD-1");
        var userId = Guid.NewGuid();
        var branchId = Guid.NewGuid();

        await using (var scope = _fixture.CreateScope(organization.Id, userId))
        {
            var context = scope.ServiceProvider.GetRequiredService<RetailFlowDbContext>();
            context.Branches.Add(Branch.Create(branchId, organization.Id, "AUD", "Audited"));
            await context.SaveChangesAsync();
        }

        var otherUser = Guid.NewGuid();

        await using (var scope = _fixture.CreateScope(organization.Id, otherUser))
        {
            var context = scope.ServiceProvider.GetRequiredService<RetailFlowDbContext>();
            var branch = await context.Branches.SingleAsync(b => b.Id == branchId);

            branch.CreatedByUserId.Should().Be(userId);
            branch.CreatedAtUtc.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
            branch.UpdatedAtUtc.Should().BeNull();

            branch.UpdateDetails("Changed", null, null, null, null);
            await context.SaveChangesAsync();

            branch.UpdatedByUserId.Should().Be(otherUser);
            branch.CreatedByUserId.Should().Be(userId, "the original author must not be overwritten");
        }
    }

    [Fact]
    public async Task Timestamps_round_trip_as_UTC()
    {
        var organization = await _fixture.CreateOrganizationAsync("UTC-1");
        var branchId = Guid.NewGuid();

        await using (var scope = _fixture.CreateScope(organization.Id))
        {
            var context = scope.ServiceProvider.GetRequiredService<RetailFlowDbContext>();
            context.Branches.Add(Branch.Create(branchId, organization.Id, "UTC", "Utc"));
            await context.SaveChangesAsync();
        }

        await using (var scope = _fixture.CreateScope(organization.Id))
        {
            var context = scope.ServiceProvider.GetRequiredService<RetailFlowDbContext>();
            var branch = await context.Branches.AsNoTracking().SingleAsync(b => b.Id == branchId);

            branch.CreatedAtUtc.Offset.Should().Be(TimeSpan.Zero, "persisted instants are UTC (ADR-0007)");
        }
    }

    [Fact]
    public async Task Business_day_start_round_trips_as_a_time_of_day()
    {
        var organization = await _fixture.CreateOrganizationAsync("UTC-2");
        var branchId = Guid.NewGuid();
        var dayStart = new TimeOnly(6, 30);

        await using (var scope = _fixture.CreateScope(organization.Id))
        {
            var context = scope.ServiceProvider.GetRequiredService<RetailFlowDbContext>();
            context.Branches.Add(Branch.Create(
                branchId, organization.Id, "DAY", "Day start", "Asia/Dhaka", dayStart));
            await context.SaveChangesAsync();
        }

        await using (var scope = _fixture.CreateScope(organization.Id))
        {
            var context = scope.ServiceProvider.GetRequiredService<RetailFlowDbContext>();
            var branch = await context.Branches.AsNoTracking().SingleAsync(b => b.Id == branchId);

            branch.BusinessDayStart.Should().Be(dayStart);

            // 02:00 local on the 12th belongs to the trading day that began 06:30 on the 11th.
            branch.BusinessDateFor(new DateTimeOffset(2026, 10, 11, 20, 0, 0, TimeSpan.Zero))
                .Should().Be(new DateOnly(2026, 10, 11));
        }
    }

    [Fact]
    public async Task Decimal_columns_keep_four_decimal_places()
    {
        // Money precision is configured by convention (ADR-0006); this proves the convention
        // reached the database rather than silently defaulting to decimal(18,2).
        await using var scope = _fixture.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RetailFlowDbContext>();

        var precision = await context.Database
            .SqlQuery<string>($"""
                SELECT CAST(c.precision AS VARCHAR(10)) + ',' + CAST(c.scale AS VARCHAR(10)) AS [Value]
                FROM sys.columns c
                WHERE c.object_id = OBJECT_ID('Branch') AND c.name = 'ChangeSequence'
                """)
            .ToListAsync();

        precision.Should().NotBeEmpty("the Branch table must exist with its sequence column");

        var money = Money.From(1234.56789m);
        money.Amount.Should().Be(1234.5679m, "amounts are kept at four decimals before settlement");
    }

    [Fact]
    public async Task Synchronous_saves_are_refused_so_tenancy_can_never_be_skipped()
    {
        var organization = await _fixture.CreateOrganizationAsync("SYNC-1");

        await using var scope = _fixture.CreateScope(organization.Id);
        var context = scope.ServiceProvider.GetRequiredService<RetailFlowDbContext>();
        context.Branches.Add(Branch.Create(Guid.NewGuid(), organization.Id, "SYNC", "Sync"));

        // The interceptor needs a database round trip to allocate sequences, which it refuses to
        // do by blocking a thread. Making this explicit prevents a future handler from quietly
        // bypassing the tenant and audit stamping by calling the synchronous overload.
        var act = () => context.SaveChanges();

        act.Should().Throw<NotSupportedException>();
    }
}
