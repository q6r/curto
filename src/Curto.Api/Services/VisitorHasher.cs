using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Curto.Api.Services;

/// <summary>
/// Pseudonymous visitor identifiers: <c>HMAC-SHA256(dailySalt, ip + NUL + userAgent)</c>, truncated to 64 bits.
/// The salt is random, lives in memory only and is replaced at every UTC midnight, so identifiers cannot be linked across
/// days and cannot be recomputed after a restart. The trade-off is deliberate: "unique visitors" means unique per day.
/// </summary>
internal sealed class VisitorHasher
{
    private sealed record DailySalt(DateOnly Day, byte[] Key);

    private DailySalt _current = new(DateOnly.MinValue, []);

    /// <summary>Computes the identifier for a request made at <paramref name="now"/>.</summary>
    /// <param name="address">Client address; null hashes as the empty string.</param>
    /// <param name="userAgent">Raw User-Agent; null hashes as the empty string.</param>
    /// <param name="now">Instant of the request; selects (and if needed rotates) the daily salt.</param>
    public long Hash(IPAddress? address, string? userAgent, DateTimeOffset now)
    {
        var salt = SaltFor(DateOnly.FromDateTime(now.UtcDateTime));

        var ip = address?.ToString() ?? string.Empty;
        var ua = userAgent ?? string.Empty;
        var data = new byte[Encoding.UTF8.GetByteCount(ip) + 1 + Encoding.UTF8.GetByteCount(ua)];
        var written = Encoding.UTF8.GetBytes(ip, data);
        data[written] = 0;
        Encoding.UTF8.GetBytes(ua, data.AsSpan(written + 1));

        Span<byte> mac = stackalloc byte[HMACSHA256.HashSizeInBytes];
        HMACSHA256.HashData(salt.Key, data, mac);
        return BinaryPrimitives.ReadInt64BigEndian(mac);
    }

    private DailySalt SaltFor(DateOnly day)
    {
        while (true)
        {
            // The salt only ever moves forward. A request that arrives "late" around midnight (or after a clock step back)
            // shares the newer salt instead of resurrecting yesterday's, so identifiers never flip-flop between salts.
            var current = Volatile.Read(ref _current);
            if (day <= current.Day)
            {
                return current;
            }

            var next = new DailySalt(day, RandomNumberGenerator.GetBytes(32));
            if (ReferenceEquals(Interlocked.CompareExchange(ref _current, next, current), current))
            {
                return next;
            }
        }
    }
}
