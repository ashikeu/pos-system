using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace RetailFlow.IntegrationTests;

/// <summary>
/// Proves the API host actually starts and serves its health endpoints. Phase 2 extends readiness
/// with database, migration and outbox-backlog checks, and this suite with a real SQL Server
/// fixture (ADR-0020).
/// </summary>
public class HealthEndpointTests : IClassFixture<WebApplicationFactory<Api.Program>>
{
    private readonly WebApplicationFactory<Api.Program> _factory;

    public HealthEndpointTests(WebApplicationFactory<Api.Program> factory) => _factory = factory;

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Health_endpoints_report_healthy(string path)
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("Healthy");
    }

    [Fact]
    public async Task Unknown_routes_return_not_found()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/does-not-exist");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
