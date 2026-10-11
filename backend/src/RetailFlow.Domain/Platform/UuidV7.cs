using System.Security.Cryptography;

namespace RetailFlow.Domain.Platform;

/// <summary>
/// Generates RFC 9562 UUID version 7 identifiers: a 48-bit Unix millisecond timestamp followed by
/// randomness, so keys are time-ordered and can be created offline on a terminal without
/// collisions (ADR-0005).
/// </summary>
/// <remarks>
/// Values generated within the same millisecond stay ordered through a 12-bit counter, so a batch
/// of sale lines created in one tick keeps its insertion order. The counter is process-wide and
/// guarded by a lock; contention is negligible compared with the database round trip that follows.
/// <para>
/// The requested instant is always embedded as given — the generator never clamps to the highest
/// timestamp it has seen. An offline terminal has to be able to stamp a record with its own
/// business instant, and correctness of the key rests on uniqueness, not on global ordering.
/// </para>
/// </remarks>
public static class UuidV7
{
    private const int CounterMask = 0x0FFF;

    private static readonly object Gate = new();
    private static long _lastTimestampMs = -1;
    private static int _counter;

    /// <summary>Creates a new identifier stamped with the current UTC time.</summary>
    public static Guid NewGuid() => NewGuid(DateTimeOffset.UtcNow);

    /// <summary>
    /// Creates a new identifier stamped with the supplied instant. Taking the clock as an argument
    /// keeps generation testable and lets callers stamp an identifier with the business instant of
    /// an offline operation.
    /// </summary>
    public static Guid NewGuid(DateTimeOffset timestamp)
    {
        var timestampMs = timestamp.ToUnixTimeMilliseconds();
        if (timestampMs < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timestamp), timestamp, "UUIDv7 cannot represent instants before the Unix epoch.");
        }

        int counter;
        lock (Gate)
        {
            if (timestampMs == _lastTimestampMs)
            {
                // Same millisecond: keep counting so a batch created in one tick stays ordered.
                // Wrapping past 4096 ids in a single millisecond falls back to a random counter;
                // uniqueness still rests on the 62 random bits below.
                _counter = _counter >= CounterMask
                    ? RandomNumberGenerator.GetInt32(CounterMask + 1)
                    : _counter + 1;
            }
            else
            {
                // A different millisecond, including one in the past: the requested instant is
                // always honoured. Identifiers are not clamped to the highest value seen, because
                // an offline terminal must be able to stamp a record with its own business instant.
                _lastTimestampMs = timestampMs;
                _counter = 0;
            }

            counter = _counter;
        }

        Span<byte> bytes = stackalloc byte[16];

        // 48-bit big-endian timestamp.
        bytes[0] = (byte)(timestampMs >> 40);
        bytes[1] = (byte)(timestampMs >> 32);
        bytes[2] = (byte)(timestampMs >> 24);
        bytes[3] = (byte)(timestampMs >> 16);
        bytes[4] = (byte)(timestampMs >> 8);
        bytes[5] = (byte)timestampMs;

        // Version 7 in the high nibble, then the 12-bit monotonic counter as rand_a.
        bytes[6] = (byte)(0x70 | ((counter >> 8) & 0x0F));
        bytes[7] = (byte)(counter & 0xFF);

        RandomNumberGenerator.Fill(bytes[8..]);

        // RFC 9562 variant: top two bits of octet 8 are 1 and 0.
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);

        return FromBigEndian(bytes);
    }

    /// <summary>
    /// Reads the embedded timestamp back out of a UUIDv7. Returns <c>null</c> when the value is not
    /// version 7, for example a key created by an older client or a non-v7 seed value.
    /// </summary>
    public static DateTimeOffset? TryGetTimestamp(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        ToBigEndian(id, bytes);

        var version = (bytes[6] & 0xF0) >> 4;
        if (version != 7)
        {
            return null;
        }

        long timestampMs = 0;
        for (var i = 0; i < 6; i++)
        {
            timestampMs = (timestampMs << 8) | bytes[i];
        }

        return DateTimeOffset.FromUnixTimeMilliseconds(timestampMs);
    }

    /// <summary>True when the value carries the UUID version 7 marker.</summary>
    public static bool IsUuidV7(Guid id) => TryGetTimestamp(id) is not null;

    // Guid stores its first three fields in machine byte order, so the canonical big-endian UUID
    // layout has to be swapped on the way in and out. Doing it here keeps every caller honest.
    private static Guid FromBigEndian(ReadOnlySpan<byte> bigEndian)
    {
        Span<byte> mixed = stackalloc byte[16];
        bigEndian.CopyTo(mixed);

        if (BitConverter.IsLittleEndian)
        {
            (mixed[0], mixed[3]) = (mixed[3], mixed[0]);
            (mixed[1], mixed[2]) = (mixed[2], mixed[1]);
            (mixed[4], mixed[5]) = (mixed[5], mixed[4]);
            (mixed[6], mixed[7]) = (mixed[7], mixed[6]);
        }

        return new Guid(mixed);
    }

    private static void ToBigEndian(Guid id, Span<byte> destination)
    {
        if (!id.TryWriteBytes(destination))
        {
            throw new ArgumentException("Destination must be 16 bytes.", nameof(destination));
        }

        if (BitConverter.IsLittleEndian)
        {
            (destination[0], destination[3]) = (destination[3], destination[0]);
            (destination[1], destination[2]) = (destination[2], destination[1]);
            (destination[4], destination[5]) = (destination[5], destination[4]);
            (destination[6], destination[7]) = (destination[7], destination[6]);
        }
    }
}
