using System.Net;
using System.Net.Sockets;

namespace Curto.Api.Infrastructure;

/// <summary>Helpers around the caller's network address. Addresses are used transiently and never stored.</summary>
internal static class ClientAddress
{
    /// <summary>Address of the connection after forwarded-header processing, with IPv4-mapped IPv6 unwrapped; null when unknown.</summary>
    /// <param name="http">Current request.</param>
    public static IPAddress? Of(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);

        var address = http.Connection.RemoteIpAddress;
        return address is { IsIPv4MappedToIPv6: true } ? address.MapToIPv4() : address;
    }

    /// <summary>
    /// Partition key for rate limiting. IPv6 clients are grouped by /64 so rotating addresses inside their own
    /// prefix does not buy extra budget.
    /// </summary>
    /// <param name="address">Client address, if known.</param>
    public static string RateLimitKey(IPAddress? address)
    {
        if (address is null)
        {
            return "unknown";
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out _);
        return Convert.ToHexString(bytes[..8]) + "/64";
    }
}
