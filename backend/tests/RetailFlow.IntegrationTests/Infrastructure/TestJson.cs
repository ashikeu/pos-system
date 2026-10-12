using System.Text.Json;
using System.Text.Json.Serialization;

namespace RetailFlow.IntegrationTests.Infrastructure;

/// <summary>
/// Serializer options that match what the API produces: camel case and enums as names. A test that
/// used the defaults would fail to read a perfectly good response, which looks like a product bug
/// and is not one.
/// </summary>
internal static class TestJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
