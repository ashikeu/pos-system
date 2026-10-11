using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RetailFlow.Application.Platform.Abstractions;
using RetailFlow.Domain.Platform;
using RetailFlow.Domain.Tenancy;
using RetailFlow.Infrastructure.Platform.Persistence;
using RetailFlow.IntegrationTests.Infrastructure;
using Xunit;

namespace RetailFlow.IntegrationTests.Platform;

/// <summary>
/// The isolation guarantees from ADR-0004 and FR-TEN-02. These are the tests that must never be
/// allowed to go red: a regression here means one shop can see another shop's trading data.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class TenantIsolationTests
{
    private readonly DatabaseFixture _fixture;

    public TenantIsolationTests(DatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_branch_of_one_tenant_is_invisible_to_another()
    {
        var alpha = await _fixture.CreateOrganizationAsync("ISO-A1");
        var beta = await _fixture.CreateOrganizationAsync("ISO-B1");

        await using (var scope = _fixture.CreateScope(alpha.Id))
        {
            var context = scope.ServiceProvider.GetRequiredService<RetailFlowDbContext>();
            context.Branches.Add(Branch.Create(Guid.NewGuid(), alpha.Id, "MAIN", "Alpha Main"));
            await context.SaveChangesAsync();
        }

        await using (var scope = _fixture.CreateScope(beta.Id))
        {
            var context = scope.ServiceProvider.GetRequiredService<RetailFlowDbContext>();

            (await context.Branches.CountAsync()).Should().Be(0);
            (await context.Branches.FirstOrDefaultAsync(b => b.Code == "MAIN")).Should().BeNull();
        }

        await using (var scope = _fixture.CreateScope(alpha.Id))
        {
            var context = scope.ServiceProvider.GetRequiredService<RetailFlowDbContext>();
            (await context.Branches.CountAsync()).Should().Be(1);
        }
    }

    [Fact]
    public async Task A_branch_cannot_be_fetched_by_id_from_another_tenant()
    {
        var alpha = await _fixture.CreateOrganizationAsync("ISO-A2");
        var beta = await _fixture.CreateOrganizationAsync("ISO-B2");
        var branchId = Guid.NewGuid();

        await using (var scope = _fixture.CreateScope(alpha.Id))
        {
            var context = scope.ServiceProvider.GetRequiredService<RetailFlowDbContext>();
            context.Branches.Add(Branch.Create(branchId, alpha.Id, "MAIN", "Alpha Main"));
            await context.SaveChangesAsync();
        }

        await using (var scope = _fixture.CreateScope(beta.Id))
        {
            var context = scope.ServiceProvider.GetRequiredService<RetailFlowDbContext>();

            // Guessing or stealing an id must not help: the filter is applied to FindAsync and to
            // explicit predicates alike (FR-TEN-02).
            (await context.Branches.FirstOrDefaultAsync(b => b.Id == branchId)).Should().BeNull();
        }
    }

    [Fact]
    public async Task Writing_a_record_owned_by_another_tenant_is_rejected()
    {
        var alpha = await _fixture.CreateOrganizationAsync("ISO-A3");
        var beta = await _fixture.CreateOrganizationAsync("ISO-B3");

        await using var scope = _fixture.CreateScope(beta.Id);
        var context = scope.ServiceProvider.GetRequiredService<RetailFlowDbContext>();

        // A handler tricked into constructing a record for another organization still cannot
        // persist it: the save interceptor compares the row against the ambient tenant.
        context.Branches.Add(Branch.Create(Guid.NewGuid(), alpha.Id, "SNEAK", "Injected"));

        var act = () => context.SaveChangesAsync();

        await act.Should().ThrowAsync<TenantMismatchException>();
    }

    [Fact]
    public async Task Modifying_another_tenants_record_is_rejected()
    {
        var alpha = await _fixture.CreateOrganizationAsync("ISO-A4");
        var beta = await _fixture.CreateOrganizationAsync("ISO-B4");
        var branchId = Guid.NewGuid();

        await using (var scope = _fixture.CreateScope(alpha.Id))
        {
            var context = scope.ServiceProvider.GetRequiredService<RetailFlowDbContext>();
            context.Branches.Add(Branch.Create(branchId, alpha.Id, "MAIN", "Alpha Main"));
            await context.SaveChangesAsync();
        }

        await using (var scope = _fixture.CreateScope(beta.Id))
        {
            var context = scope.ServiceProvider.GetRequiredService<RetailFlowDbContext>();

            // Loaded with the filter bypassed, as a careless cross-tenant query would.
            var branch = await context.Branches
                .IgnoreQueryFilters()
                .SingleAsync(b => b.Id == branchId);

            branch.UpdateDetails("Hijacked", null, null, null, null);

            var act = () => context.SaveChangesAsync();

            await act.Should().ThrowAsync<TenantMismatchException>();
        }
    }

    [Fact]
    public async Task Without_tenant_context_a_tenant_query_returns_nothing()
    {
        var alpha = await _fixture.CreateOrganizationAsync("ISO-A5");

        await using (var scope = _fixture.CreateScope(alpha.Id))
        {
            var context = scope.ServiceProvider.GetRequiredService<RetailFlowDbContext>();
            context.Branches.Add(Branch.Create(Guid.NewGuid(), alpha.Id, "MAIN", "Alpha Main"));
            await context.SaveChangesAsync();
        }

        // No tenant established: the filter must fail closed. Returning every tenant's rows here
        // would turn one forgotten middleware registration into a data breach.
        await using var anonymous = _fixture.CreateScope();
        var anonymousContext = anonymous.ServiceProvider.GetRequiredService<RetailFlowDbContext>();

        (await anonymousContext.Branches.CountAsync()).Should().Be(0);
        (await anonymousContext.Organizations.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Without_tenant_context_a_tenant_write_is_rejected()
    {
        var alpha = await _fixture.CreateOrganizationAsync("ISO-A6");

        await using var scope = _fixture.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RetailFlowDbContext>();
        context.Branches.Add(Branch.Create(Guid.NewGuid(), alpha.Id, "NOCTX", "No context"));

        var act = () => context.SaveChangesAsync();

        await act.Should().ThrowAsync<MissingTenantContextException>();
    }

    [Fact]
    public async Task Reading_the_tenant_id_without_context_throws_rather_than_returning_empty()
    {
        await using var scope = _fixture.CreateScope();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();

        tenant.HasTenant.Should().BeFalse();
        tenant.OrganizationIdOrNull.Should().BeNull();

        var act = () => tenant.OrganizationId;

        act.Should().Throw<MissingTenantContextException>();
    }

    [Fact]
    public async Task An_organization_only_sees_itself()
    {
        var alpha = await _fixture.CreateOrganizationAsync("ISO-A7");
        await _fixture.CreateOrganizationAsync("ISO-B7");

        await using var scope = _fixture.CreateScope(alpha.Id);
        var context = scope.ServiceProvider.GetRequiredService<RetailFlowDbContext>();

        var visible = await context.Organizations.ToListAsync();

        visible.Should().HaveCount(1);
        visible[0].Id.Should().Be(alpha.Id);
    }
}
