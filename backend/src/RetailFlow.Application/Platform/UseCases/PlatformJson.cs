using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RetailFlow.Application.Platform.UseCases;

/// <summary>
/// Serialization used for stored artefacts — idempotency response snapshots, outbox payloads and
/// audit diffs. Pinned deliberately: these values are read back weeks later, and a change in
/// casing or enum format would make old records unreadable.
/// </summary>
public static class PlatformJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    public static string Serialize<TValue>(TValue value) => JsonSerializer.Serialize(value, Options);

    public static TValue? Deserialize<TValue>(string json) => JsonSerializer.Deserialize<TValue>(json, Options);

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
            NumberHandling = JsonNumberHandling.Strict,
        };

        options.Converters.Add(new JsonStringEnumConverter());

        // Frozen with the reflection resolver populated: a shared instance mutated later would
        // change how records written weeks ago are read back.
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

/// <summary>
/// Fingerprints a request so an idempotency key presented with a different body can be rejected
/// instead of silently returning the earlier result (ADR-0010).
/// </summary>
public static class RequestFingerprint
{
    /// <summary>Returns a lowercase hex SHA-256 of the canonical JSON form of the request.</summary>
    public static string Compute<TRequest>(TRequest request)
    {
        var json = PlatformJson.Serialize(request);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
