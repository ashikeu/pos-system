using System.Net;
using System.Text.Json;
using FluentAssertions;
using RetailFlow.IntegrationTests.Infrastructure;
using Xunit;

namespace RetailFlow.IntegrationTests;

/// <summary>
/// The endpoints a load balancer and an operator rely on. Liveness must stay cheap and independent
/// of dependencies; readiness must actually check the database and the schema version, so a
/// deployment with a missing migration never takes traffic (docs/03-architecture.md §10).
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class HealthEndpointTests : IAsyncLifetime
{
    private readonly DatabaseFixture _fixture;
    private ApiFactory? _factory;

    public HealthEndpointTests(DatabaseFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {
        _factory = new ApiFactory(_fixture.ConnectionString);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync().ConfigureAwait(false);
        }
    }

    private ApiFactory Factory => _factory ?? throw new InvalidOperationException("Not initialised.");

    [Fact]
    public async Task Liveness_is_healthy_without_touching_the_database()
    {
        using var client = Factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("Healthy");
    }

    [Fact]
    public async Task Readiness_reports_the_database_and_outbox_state()
    {
        using var client = Factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        root.GetProperty("status").GetString().Should().Be("Healthy");

        var check = root.GetProperty("checks").EnumerateArray().Single();
        check.GetProperty("name").GetString().Should().Be("database");
        check.GetProperty("data").GetProperty("outboxBacklog").GetInt32().Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task Every_response_carries_a_correlation_id()
    {
        using var client = Factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative));

        response.Headers.TryGetValues("X-Correlation-Id", out var values).Should().BeTrue();
        values!.Single().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_client_supplied_correlation_id_is_echoed_back()
    {
        using var client = Factory.CreateClient();
        var correlationId = "pos-terminal-7-" + Guid.NewGuid().ToString("N")[..8];

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/health/live", UriKind.Relative));
        request.Headers.Add("X-Correlation-Id", correlationId);

        using var response = await client.SendAsync(request);

        response.Headers.GetValues("X-Correlation-Id").Single().Should().Be(correlationId);
    }

    [Fact]
    public async Task An_unknown_route_returns_not_found()
    {
        using var client = Factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/api/v1/does-not-exist", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
