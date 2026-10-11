using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RetailFlow.Application.Identity.Abstractions;

namespace RetailFlow.Infrastructure.Identity.Services;

/// <summary>
/// RFC 6238 time-based one-time passwords, HMAC-SHA1 with a 30-second step and 6 digits — the
/// combination every authenticator app supports (FR-SEC-06).
/// </summary>
/// <remarks>
/// Implemented directly rather than taken from a package: the algorithm is small, fully specified,
/// and verifiable against the RFC's published test vectors, which the unit tests do. That is a
/// better trade than another dependency inside the authentication path.
/// </remarks>
internal sealed class TotpService : ITotpService
{
    private const int Digits = 6;
    private const int StepSeconds = 30;
    private const int SecretBytes = 20;

    /// <summary>
    /// Steps of drift accepted on either side. One step (±30 s) tolerates a phone whose clock is
    /// slightly off without widening the window a stolen code stays valid in.
    /// </summary>
    private const int DriftSteps = 1;

    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public string CreateSecret() => ToBase32(RandomNumberGenerator.GetBytes(SecretBytes));

    public bool TryValidate(
        string secret,
        string code,
        DateTimeOffset nowUtc,
        long lastUsedTimeStep,
        out long timeStep)
    {
        timeStep = 0;

        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        var normalizedCode = code.Trim().Replace(" ", string.Empty, StringComparison.Ordinal);
        if (normalizedCode.Length != Digits || !normalizedCode.All(char.IsAsciiDigit))
        {
            return false;
        }

        byte[] key;
        try
        {
            key = FromBase32(secret);
        }
        catch (FormatException)
        {
            return false;
        }

        var currentStep = nowUtc.ToUnixTimeSeconds() / StepSeconds;

        for (var offset = -DriftSteps; offset <= DriftSteps; offset++)
        {
            var candidateStep = currentStep + offset;

            // A code from a step that was already accepted is refused, so a code seen over the
            // shoulder or recovered from a log cannot be replayed within its window.
            if (candidateStep <= lastUsedTimeStep)
            {
                continue;
            }

            var expected = Compute(key, candidateStep);

            if (CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(expected),
                    Encoding.ASCII.GetBytes(normalizedCode)))
            {
                timeStep = candidateStep;
                return true;
            }
        }

        return false;
    }

    public string BuildProvisioningUri(string secret, string issuer, string accountName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        var issuerLabel = Uri.EscapeDataString(string.IsNullOrWhiteSpace(issuer) ? "RetailFlow" : issuer);
        var account = Uri.EscapeDataString(string.IsNullOrWhiteSpace(accountName) ? "user" : accountName);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"otpauth://totp/{issuerLabel}:{account}?secret={secret}&issuer={issuerLabel}&algorithm=SHA1&digits={Digits}&period={StepSeconds}");
    }

    /// <summary>Computes the code for a specific time step; exposed for the RFC vector tests.</summary>
    internal static string ComputeForStep(byte[] key, long timeStep) => Compute(key, timeStep);

    [SuppressMessage(
        "Security",
        "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification = "RFC 6238 specifies HMAC-SHA1 for TOTP, and it is what authenticator apps implement. " +
            "The HMAC is keyed with 160 bits of entropy and the code lives for 30 seconds, so SHA-1 collision " +
            "resistance is irrelevant here. Using SHA-256 would make the codes incompatible with the apps.")]
    private static string Compute(byte[] key, long timeStep)
    {
        var counter = new byte[8];
        var value = timeStep;

        for (var i = 7; i >= 0; i--)
        {
            counter[i] = (byte)(value & 0xFF);
            value >>= 8;
        }

        var mac = HMACSHA1.HashData(key, counter);

        // Dynamic truncation, RFC 4226 §5.4.
        var offset = mac[^1] & 0x0F;
        var binary = ((mac[offset] & 0x7F) << 24)
            | ((mac[offset + 1] & 0xFF) << 16)
            | ((mac[offset + 2] & 0xFF) << 8)
            | (mac[offset + 3] & 0xFF);

        var otp = binary % (int)Math.Pow(10, Digits);
        return otp.ToString(CultureInfo.InvariantCulture).PadLeft(Digits, '0');
    }

    private static string ToBase32(ReadOnlySpan<byte> data)
    {
        var builder = new StringBuilder(((data.Length + 4) / 5) * 8);
        var bits = 0;
        var accumulator = 0;

        foreach (var b in data)
        {
            accumulator = (accumulator << 8) | b;
            bits += 8;

            while (bits >= 5)
            {
                builder.Append(Base32Alphabet[(accumulator >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            builder.Append(Base32Alphabet[(accumulator << (5 - bits)) & 31]);
        }

        return builder.ToString();
    }

    internal static byte[] FromBase32(string value)
    {
        var normalized = value.Trim()
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .TrimEnd('=')
            .ToUpperInvariant();

        if (normalized.Length == 0)
        {
            throw new FormatException("The secret is empty.");
        }

        var bytes = new List<byte>(normalized.Length * 5 / 8);
        var bits = 0;
        var accumulator = 0;

        foreach (var c in normalized)
        {
            var index = Base32Alphabet.IndexOf(c, StringComparison.Ordinal);
            if (index < 0)
            {
                throw new FormatException($"'{c}' is not a base32 character.");
            }

            accumulator = (accumulator << 5) | index;
            bits += 5;

            if (bits >= 8)
            {
                bytes.Add((byte)((accumulator >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        return [.. bytes];
    }
}
