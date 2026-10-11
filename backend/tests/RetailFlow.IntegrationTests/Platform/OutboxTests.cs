using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RetailFlow.Application.Platform.Abstractions;
using RetailFlow.Infrastructure.Platform.Persistence;
using RetailFlow.Infrastructure.Platform.Persistence.Records;
using RetailFlow.Infrastructure.Platform.Services;
using RetailFlow.IntegrationTests.Infrastructure;
using Xunit;

namespace RetailFlow.IntegrationTests.Platform;

/// <summary>
/// The transactional outbox (ADR-0011): events are written with the change that caused them, then
/// delivered at least once with backoff, and dead-lettered rather than retried forever.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class OutboxTests
{
    private readonly DatabaseFixture _fixture;

    public OutboxTests(DatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_published_event_is_stored_pending_with_its_tenant_and_correlation_id()
    {
        var organization = await _fixture.CreateOrganizationAsync("OBX-1");
        var eventId = Guid.NewGuid();

        string correlationId;

        await using (var scope = _fixture.CreateScope(organization.Id))
        {
            correlationId = scope.ServiceProvider.GetRequiredService<ICorrelationContext>().CorrelationId;
            var publisher = scope.ServiceProvider.GetRequiredService<IEventPublisher>();
            var context = scope.ServiceProvider.GetRequiredService<RetailFlowDbContext>();

            await publisher.PublishAsync(new TestEvent(eventId, DateTimeOffset.UtcNow, "stored"));
            await context.SaveChangesAsync();
        }

        await using (var scope = _fixture.CreateScope(organization.Id))
        {
            var context = scope.ServiceProvider.GetRequiredService<RetailFlowDbContext>();
            var message = await context.OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == eventId);

            message.Status.Should().Be(OutboxMessageStatus.Pending);
            message.MessageType.Should().Be(TestEvent.Type);
            message.OrganizationId.Should().Be(organization.Id);
            message.CorrelationId.Should().Be(correlationId);
            message.PayloadJson.Should().Contain("stored");
            message.AttemptCount.Should().Be(0);
        }
    }

    [Fact]
    public async Task An_event_published_in_a_rolled_back_transaction_is_never_delivered()
    {
        var organization = await _fixture.CreateOrganizationAsync("OBX-2");
        var eventId = Guid.NewGuid();

        await using (var scope = _fixture.CreateScope(organization.Id))
        {
            var context = scope.ServiceProvider.GetRequiredService<RetailFlowDbContext>();
            var publisher = scope.ServiceProvider.GetRequiredService<IEventPublisher>();

            await using var transaction = await context.Database.BeginTransactionAsync();
            await publisher.PublishAsync(new TestEvent(eventId, DateTimeOffset.UtcNow, "rolled back"));
            await context.SaveChangesAsync();
            await transaction.RollbackAsync();
        }

        await using (var scope = _fixture.CreateScope(organization.Id))
        {
            var context = scope.ServiceProvider.GetRequiredService<RetailFlowDbContext>();

            // This is the whole point of the pattern: no event for a change that did not happen.
            (await context.OutboxMessages.CountAsync(m => m.Id == eventId)).Should().Be(0);
        }
    }

    [Fact]
    public async Task The_dispatcher_delivers_a_message_and_marks_it_processed()
    {
        var organization = await _fixture.CreateOrganizationAsync("OBX-3");
        var sink = _fixture.Services.GetRequiredService<TestEventSink>();
        sink.Reset();

        var eventId = Guid.NewGuid();
        await PublishAsync(organization.Id, new TestEvent(eventId, DateTimeOffset.UtcNow, "deliver-me"));

        var handled = await DispatchAsync();

        handled.Should().BeGreaterThan(0);
        sink.Payloads.Should().ContainSingle(p => p.Contains("deliver-me", StringComparison.Ordinal));

        await using var scope = _fixture.CreateScope(organization.Id);
        var context = scope.ServiceProvider.GetRequiredService<RetailFlowDbContext>();
        var message = await context.OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == eventId);

        message.Status.Should().Be(OutboxMessageStatus.Processed);
        message.ProcessedAtUtc.Should().NotBeNull();
        message.LeaseOwner.Should().BeNull();
    }

    [Fact]
    public async Task A_failing_handler_schedules_a_retry_and_eventually_dead_letters()
    {
        var organization = await _fixture.CreateOrganizationAsync("OBX-4");
        var sink = _fixture.Services.GetRequiredService<TestEventSink>();
        sink.Reset();

        // MaxAttempts is 3 in the test configuration; fail more often than that.
        sink.RemainingFailures = 5;

        var eventId = Guid.NewGuid();
        await PublishAsync(organization.Id, new TestEvent(eventId, DateTimeOffset.UtcNow, "always-fails"));

        await DispatchAsync();

        await using (var scope = _fixture.CreateScope(organization.Id))
        {
            var context = scope.ServiceProvider.GetRequiredService<RetailFlowDbContext>();
            var message = await context.OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == eventId);

            message.Status.Should().Be(OutboxMessageStatus.Failed);
            message.AttemptCount.Should().Be(1);
            message.LastError.Should().Contain("on purpose");
            message.NextAttemptAtUtc.Should().BeAfter(message.CreatedAtUtc, "a retry must be scheduled, not immediate");
            message.LeaseOwner.Should().BeNull("a failed message releases its lease");
        }

        // The dispatcher only picks up messages whose next attempt is due, so the retries are
        // forced by making them due.
        await ForceDueAsync(eventId);
        await DispatchAsync();
        await ForceDueAsync(eventId);
        await DispatchAsync();

        await using (var scope = _fixture.CreateScope(organization.Id))
        {
            var context = scope.ServiceProvider.GetRequiredService<RetailFlowDbContext>();
            var message = await context.OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == eventId);

            message.AttemptCount.Should().Be(3);
            message.Status.Should().Be(
                OutboxMessageStatus.DeadLettered,
                "retries are finite; a poison message must surface instead of looping forever");
        }
    }

    [Fact]
    public async Task A_message_with_no_handler_is_marked_processed_rather_than_retried()
    {
        var organization = await _fixture.CreateOrganizationAsync("OBX-5");
        var eventId = Guid.NewGuid();

        await using (var scope = _fixture.CreateScope(organization.Id))
        {
            var context = scope.ServiceProvider.GetRequiredService<RetailFlowDbContext>();
            var publisher = scope.ServiceProvider.GetRequiredService<IEventPublisher>();

            await publisher.PublishAsync(new UnconsumedEvent(eventId, DateTimeOffset.UtcNow));
            await context.SaveChangesAsync();
        }

        await DispatchAsync();

        await using (var scope = _fixture.CreateScope(organization.Id))
        {
            var context = scope.ServiceProvider.GetRequiredService<RetailFlowDbContext>();
            var message = await context.OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == eventId);

            message.Status.Should().Be(OutboxMessageStatus.Processed);
        }
    }

    private async Task PublishAsync(Guid organizationId, TestEvent domainEvent)
    {
        await using var scope = _fixture.CreateScope(organizationId);
        var context = scope.ServiceProvider.GetRequiredService<RetailFlowDbContext>();
        var publisher = scope.ServiceProvider.GetRequiredService<IEventPublisher>();

        await publisher.PublishAsync(domainEvent);
        await context.SaveChangesAsync();
    }

    private async Task<int> DispatchAsync()
    {
        await using var scope = _fixture.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<OutboxDispatcher>();
        return await dispatcher.DispatchBatchAsync("integration-test");
    }

    private async Task ForceDueAsync(Guid messageId)
    {
        await using var scope = _fixture.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RetailFlowDbContext>();

        await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE [OutboxMessage]
            SET [NextAttemptAtUtc] = DATEADD(minute, -1, SYSUTCDATETIME())
            WHERE [Id] = {messageId}
            """);
    }

    private sealed record UnconsumedEvent(Guid EventId, DateTimeOffset OccurredAtUtc) : Domain.Platform.IDomainEvent
    {
        public string EventType => "test.nobody-listens";
    }
}
