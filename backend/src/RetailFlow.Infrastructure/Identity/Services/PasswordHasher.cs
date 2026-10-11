using System.Security.Cryptography;
using RetailFlow.Application.Identity.Abstractions;

namespace RetailFlow.Infrastructure.Identity.Services;

/// <summary>
/// PBKDF2-HMAC-SHA512 password hashing with a versioned, self-describing format (ADR-0012).
/// </summary>
/// <remarks>
/// <para>
/// Format: <c>v1.{iterations}.{base64 salt}.{base64 hash}</c>. Storing the parameters alongside the
/// hash is what allows the iteration count to be raised later: an old hash still verifies with its
/// own parameters, and the sign-in path re-hashes it with the current ones while it has the
/// plaintext.
/// </para>
/// <para>
/// PBKDF2 was chosen over Argon2id to avoid a native dependency in v1; the port exists so that
/// swap is a registration change plus rehash-on-login, and it is tracked as a hardening item.
/// </para>
/// </remarks>
internal sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    private const string Version = "v1";
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const char Separator = '.';

    private readonly int _iterations;

    public Pbkdf2PasswordHasher(AuthenticationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // A floor is enforced: a mis-set configuration value must not quietly weaken every
        // password in the database.
        _iterations = Math.Max(100_000, options.PasswordIterations);
    }

    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Derive(password, salt, _iterations);

        return string.Join(
            Separator,
            Version,
            _iterations.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToBase64String(salt),
            Convert.ToBase64String(hash));
    }

    public PasswordVerificationResult Verify(string hash, string password)
    {
        if (string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(password))
        {
            return PasswordVerificationResult.Failed;
        }

        var parts = hash.Split(Separator);
        if (parts.Length != 4
            || !string.Equals(parts[0], Version, StringComparison.Ordinal)
            || !int.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var iterations)
            || iterations < 1)
        {
            return PasswordVerificationResult.Failed;
        }

        byte[] salt;
        byte[] expected;

        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            // A corrupt stored hash is a failure to verify, not an exception to the caller: the
            // sign-in path should report invalid credentials and log, not return a 500.
            return PasswordVerificationResult.Failed;
        }

        var actual = Derive(password, salt, iterations, expected.Length);

        // Fixed-time comparison: a short-circuiting compare leaks how much of the hash matched.
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            return PasswordVerificationResult.Failed;
        }

        return iterations < _iterations
            ? PasswordVerificationResult.SuccessRehashNeeded
            : PasswordVerificationResult.Success;
    }

    private static byte[] Derive(string password, byte[] salt, int iterations, int size = HashSize) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA512, size);
}

/// <summary>
/// Random secrets and their digests (ADR-0012). Refresh tokens, device secrets and reset tokens are
/// high-entropy values, so a single SHA-256 is the right digest: it is fast enough to look up by
/// hash on every refresh, and there is nothing to brute-force.
/// </summary>
internal sealed class SecureTokenGenerator : ISecureTokenGenerator
{
    private const int TokenBytes = 32;

    public string CreateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(TokenBytes);

        // URL-safe so the value can travel in a link or a header without escaping.
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    public string CreateNumericCode(int digits = 6)
    {
        if (digits is < 4 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(digits), digits, "Codes must be 4 to 10 digits.");
        }

        var max = (int)Math.Pow(10, digits);
        var value = RandomNumberGenerator.GetInt32(max);

        return value.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(digits, '0');
    }

    public string Hash(string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);

        var bytes = System.Text.Encoding.UTF8.GetBytes(token);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
    }
}

/// <summary>
/// Encrypts values that must be read back but must not be readable from a database backup — today
/// only TOTP secrets. AES-GCM with a key from configuration (ADR-0023); the nonce is random per
/// value and stored with the ciphertext, and the tag makes tampering detectable.
/// </summary>
internal sealed class AesGcmSecretProtector : ISecretProtector
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _key;

    public AesGcmSecretProtector(AuthenticationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.SecretProtectionKey))
        {
            throw new InvalidOperationException(
                "Security:SecretProtectionKey is not configured. Set a base64 32-byte key in appsettings (ADR-0023).");
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(options.SecretProtectionKey);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                "Security:SecretProtectionKey must be base64-encoded.", ex);
        }

        if (key.Length is not (16 or 24 or 32))
        {
            throw new InvalidOperationException(
                "Security:SecretProtectionKey must decode to 16, 24 or 32 bytes.");
        }

        _key = key;
    }

    public string Protect(string plaintext)
    {
        ArgumentException.ThrowIfNullOrEmpty(plaintext);

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plainBytes = System.Text.Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plainBytes, cipher, tag);

        var payload = new byte[NonceSize + TagSize + cipher.Length];
        nonce.CopyTo(payload, 0);
        tag.CopyTo(payload, NonceSize);
        cipher.CopyTo(payload, NonceSize + TagSize);

        return Convert.ToBase64String(payload);
    }

    public string Unprotect(string protectedValue)
    {
        ArgumentException.ThrowIfNullOrEmpty(protectedValue);

        var payload = Convert.FromBase64String(protectedValue);
        if (payload.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("The protected value is malformed.");
        }

        var nonce = payload.AsSpan(0, NonceSize);
        var tag = payload.AsSpan(NonceSize, TagSize);
        var cipher = payload.AsSpan(NonceSize + TagSize);
        var plain = new byte[cipher.Length];

        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain);

        return System.Text.Encoding.UTF8.GetString(plain);
    }
}
