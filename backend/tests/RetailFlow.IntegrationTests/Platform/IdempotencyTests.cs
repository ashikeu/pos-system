using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RetailFlow.Application.Platform.Abstractions;
using RetailFlow.Application.Platform.UseCases;
using RetailFlow.Infrastructure.Platform.Persistence;
using RetailFlow.Infrastructure.Platform.Persistence.Records;
using RetailFlow.IntegrationTests.Infrastructure;
using Xunit;

namespace RetailFlow.IntegrationTests.Platform;

/// <summary>
/// The guarantee that makes offline selling safe (ADR-0010, FR-OFF-06): retrying an operation
/// whose acknowledgement was lost returns the original outcome instead of creating a second one.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class IdempotencyTests
{
    private readonly DatabaseFixture _fixture;

    public IdempotencyTests(DatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_replayed_request_returns_the_original_response_and_does_not_run_again()
    {
        var organization = await _fixture.CreateOrganizationAsync("IDEM-1");
        var key = Guid.NewGuid().ToString("N");
        var request = new CreateTestBranchRequest(key, "B1");

        var counter = _fixture.Services.GetRequiredService<InvocationCounter>();
        counter.Reset();

        CreateTestBranchResponse first;
        await using (var scope = _fixture.CreateScope(organization.Id))
        {
            var useCase = scope.ServiceProvider
                .GetRequiredService<IUseCase<CreateTestBranchRequest, CreateTestBranchResponse>>();
            var result = await useCase.HandleAsync(request);

            result.IsSuccess.Should().BeTrue();
            first = result.Value;
        }

        // The terminal never saw the acknowledgement and retries with the same key.
        await using (var scope = _fixture.CreateScope(organization.Id))
        {
            var useCase = scope.ServiceProvider
                .GetRequiredService<IUseCase<CreateTestBranchRequest, CreateTestBranchResponse>>();
            var replay = await useCase.HandleAsync(request);

            replay.IsSuccess.Should().BeTrue();
            replay.Value.Id.Should().Be(first.Id);
            replay.Value.Code.Should().Be(first.Code);
            replay.Value.InvocationNumber.Should().Be(first.InvocationNumber);
        }

        counter.Count.Should().Be(1, "the handler must run exactly once for one idempotency key");

        await using (var verification = _fixture.CreateScope(organization.Id))
        {
            var context = verification.ServiceProvider.GetRequiredService<RetailFlowDbContext>();
            (await context.Branches.CountAsync(b => b.Code == "B1"))
                .Should().Be(1, "a lost acknowledgement must not create a second record");
        }
    }

    [Fact]
    public async Task The_same_key_with_a_different_body_is_rejected()
    {
        var organization = await _fixture.CreateOrganizationAsync("IDEM-2");
        var key = Guid.NewGuid().ToString("N");

        await using (var scope = _fixture.CreateScope(organization.Id))
        {
            var useCase = scope.ServiceProvider
                .GetRequiredService<IUseCase<CreateTestBranchRequest, CreateTestBranchResponse>>();
            (await useCase.HandleAsync(new CreateTestBranchRequest(key, "B2"))).IsSuccess.Should().BeTrue();
        }

        await using (var scope = _fixture.CreateScope(organization.Id))
        {
            var useCase = scope.ServiceProvider
                .GetRequiredService<IUseCase<CreateTestBranchRequest, CreateTestBranchResponse>>();
            var result = await useCase.HandleAsync(new CreateTestBranchRequest(key, "DIFFERENT"));

            result.IsFailure.Should().BeTrue();
            result.Error.Code.Should().Be("platform.idempotency_key_reuse");
        }
    }

    [Fact]
    public async Task A_rejected_operation_leaves_nothing_behind_and_its_key_can_be_reused()
    {
        var organization = await _fixture.CreateOrganizationAsync("IDEM-3");
        var key = Guid.NewGuid().ToString("N");

        await using (var scope = _fixture.CreateScope(organization.Id))
        {
            var useCase = scope.ServiceProvider
                .GetRequiredService<IUseCase<CreateTestBranchRequest, CreateTestBranchResponse>>();
            var result = await useCase.HandleAsync(new CreateTestBranchRequest(key, "B3", FailAfterWrite: true));

            result.IsFailure.Should().BeTrue();
            result.Error.Code.Should().Be("test.rejected_after_write");
        }

        await using (var scope = _fixture.CreateScope(organization.Id))
        {
            var context = scope.ServiceProvider.GetRequiredService<RetailFlowDbContext>();

            (await context.Branches.CountAsync(b => b.Code == "B3"))
                .Should().Be(0, "the write made before the rejection must roll back");

            (await context.IdempotencyRecords.CountAsync(r => r.Key == key))
                .Should().Be(0, "a rejected operation must stay retryable with the same key");
        }

        // The client fixes the cause and retries with the same key: it must now succeed.
        await using (var scope = _fixture.CreateScope(organization.Id))
        {
            var useCase = scope.ServiceProvider
                .GetRequiredService<IUseCase<CreateTestBranchRequest, CreateTestBranchResponse>>();
            var retry = await useCase.HandleAsync(new CreateTestBranchRequest(key, "B3"));

            retry.IsSuccess.Should().BeTrue();
        }
    }

    [Fact]
    public async Task A_key_is_scoped_to_its_tenant()
    {
        var alpha = await _fixture.CreateOrganizationAsync("IDEM-4A");
        var beta = await _fixture.CreateOrganizationAsync("IDEM-4B");
        var key = Guid.NewGuid().ToString("N");

        await using (var scope = _fixture.CreateScope(alpha.Id))
        {
            var useCase = scope.ServiceProvider
                .GetRequiredService<IUseCase<CreateTestBranchRequest, CreateTestBranchResponse>>();
            (await useCase.HandleAsync(new CreateTestBranchRequest(key, "SHARED"))).IsSuccess.Should().BeTrue();
        }

        await using (var scope = _fixture.CreateScope(beta.Id))
        {
            var useCase = scope.ServiceProvider
                .GetRequiredService<IUseCase<CreateTestBranchRequest, CreateTestBranchResponse>>();

            // Two terminals in different organizations can legitimately generate the same key.
            var result = await useCase.HandleAsync(new CreateTestBranchRequest(key, "SHARED"));

            result.IsSuccess.Should().BeTrue();
        }
    }

    [Fact]
    public async Task A_stored_record_keeps_the_declared_success_status_and_resource_id()
    {
        var organization = await _fixture.CreateOrganizationAsync("IDEM-5");
        var key = Guid.NewGuid().ToString("N");

        Guid branchId;
        await using (var scope = _fixture.CreateScope(organization.Id))
        {
            var useCase = scope.ServiceProvider
                .GetRequiredService<IUseCase<CreateTestBranchRequest, CreateTestBranchResponse>>();
            branchId = (await useCase.HandleAsync(new CreateTestBranchRequest(key, "B5"))).Value.Id;
        }

        await using (var scope = _fixture.CreateScope(organization.Id))
        {
            var context = scope.ServiceProvider.GetRequiredService<RetailFlowDbContext>();
            var record = await context.IdempotencyRecords.SingleAsync(r => r.Key == key);

            record.Status.Should().Be(IdempotencyRecordStatus.Completed);
            record.HttpStatusCode.Should().Be(201);
            record.ResourceId.Should().Be(branchId);
            record.ResponseJson.Should().NotBeNullOrWhiteSpace();
            record.ExpiresAtUtc.Should().BeAfter(record.CreatedAtUtc);
        }
    }

    [Fact]
    public async Task An_in_flight_key_tells_the_client_to_retry_rather_than_duplicating_work()
    {
        var organization = await _fixture.CreateOrganizationAsync("IDEM-6");
        var key = Guid.NewGuid().ToString("N");
        const string endpoint = "test.endpoint";
        const string hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        await using (var scope = _fixture.CreateScope(organization.Id))
        {
            var store = scope.ServiceProvider.GetRequiredService<IIdempotencyStore>();
            var begin = await store.BeginAsync(
                organization.Id, key, endpoint, hash, TimeSpan.FromDays(1));

            begin.Outcome.Should().Be(IdempotencyOutcome.New);
        }

        await using (var scope = _fixture.CreateScope(organization.Id))
        {
            var store = scope.ServiceProvider.GetRequiredService<IIdempotencyStore>();
            var second = await store.FindAsync(organization.Id, key, endpoint, hash);

            second.Outcome.Should().Be(IdempotencyOutcome.InProgress);
        }
    }
}
