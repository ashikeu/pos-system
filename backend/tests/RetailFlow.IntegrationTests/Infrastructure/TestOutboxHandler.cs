using RetailFlow.Application.Platform.Abstractions;
using RetailFlow.Domain.Platform;

namespace RetailFlow.IntegrationTests.Infrastructure;

/// <summary>A domain event used to exercise the outbox end to end.</summary>
public sealed record TestEvent(Guid EventId, DateTimeOffset OccurredAtUtc, string Note) : IDomainEvent
{
    public const string Type = "test.event";

    public string EventType => Type;
}

/// <summary>
/// Records what the outbox delivered and how often, and can be told to fail a number of times so
/// retry and dead-lettering can be observed.
/// </summary>
public sealed class TestEventSink
{
    private readonly List<string> _payloads = [];
    private readonly object _gate = new();

    public int RemainingFailures { get; set; }

    public IReadOnlyList<string> Payloads
    {
        get
        {
            lock (_gate)
            {
                return _payloads.ToArray();
            }
        }
    }

    public void Record(string payload)
    {
        lock (_gate)
        {
            _payloads.Add(payload);
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _payloads.Clear();
            RemainingFailures = 0;
        }
    }
}

public sealed class TestOutboxHandler : IOutboxMessageHandler
{
    private readonly TestEventSink _sink;

    public TestOutboxHandler(TestEventSink sink) => _sink = sink;

    public string MessageType => TestEvent.Type;

    public Task HandleAsync(string payloadJson, CancellationToken cancellationToken = default)
    {
        if (_sink.RemainingFailures > 0)
        {
            _sink.RemainingFailures--;
            throw new InvalidOperationException("Handler failed on purpose.");
        }

        _sink.Record(payloadJson);
        return Task.CompletedTask;
    }
}
