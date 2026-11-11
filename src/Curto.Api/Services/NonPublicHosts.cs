using System.Net;
using System.Net.Sockets;

namespace Curto.Api.Services;

/// <summary>
/// Best-effort classification of hosts that are not on the public internet. It looks at literal addresses and well-known
/// local names only; it never resolves DNS (a name that resolves to a private address is not detected).
/// </summary>
internal static class NonPublicHosts
{
    private static readonly string[] LocalSuffixes = [".localhost", ".local", ".internal", ".localdomain", ".home.arpa", ".lan"];

    /// <summary>True when the URL's host is a loopback/private/link-local address or a local-only name.</summary>
    /// <param name="uri">Absolute URL.</param>
    public static bool Matches(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var host = uri.DnsSafeHost.TrimEnd('.');
        if (IPAddress.TryParse(host, out var address))
        {
            return IsNonPublic(address);
        }

        // A name without a dot resolves through the local search domain (intranet hosts, "localhost").
        if (!host.Contains('.', StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var suffix in LocalSuffixes)
        {
            if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True for addresses that are not routable on the public internet.</summary>
    /// <param name="address">IPv4 or IPv6 address.</param>
    public static bool IsNonPublic(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsNonPublicV4(address.GetAddressBytes()),
            AddressFamily.InterNetworkV6 => IsNonPublicV6(address),
            _ => true,
        };
    }

    private static bool IsNonPublicV4(ReadOnlySpan<byte> b) =>
        b[0] is 0 or 10 or 127
        || b[0] >= 224
        || (b[0] == 100 && (b[1] & 0xC0) == 64)        // 100.64.0.0/10 carrier-grade NAT
        || (b[0] == 169 && b[1] == 254)                // link-local, cloud metadata endpoints
        || (b[0] == 172 && (b[1] & 0xF0) == 16)        // 172.16.0.0/12
        || (b[0] == 192 && b[1] == 168)                // 192.168.0.0/16
        || (b[0] == 192 && b[1] == 0 && b[2] == 0)     // 192.0.0.0/24 IETF protocol assignments
        || (b[0] == 198 && (b[1] & 0xFE) == 18);       // 198.18.0.0/15 benchmarking

    private static bool IsNonPublicV6(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            return IsNonPublicV4(address.MapToIPv4().GetAddressBytes());
        }

        if (IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.IPv6Any)
            || address.IsIPv6LinkLocal
            || address.IsIPv6SiteLocal
            || address.IsIPv6UniqueLocal
            || address.IsIPv6Multicast)
        {
            return true;
        }

        Span<byte> b = stackalloc byte[16];
        address.TryWriteBytes(b, out _);

        // Transition mechanisms that embed an IPv4 address: IPv4-compatible (::a.b.c.d), NAT64 (64:ff9b::/96), 6to4 (2002::/16).
        if (b[..12].IndexOfAnyExcept((byte)0) < 0)
        {
            return IsNonPublicV4(b[12..]);
        }

        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B && b[4..12].IndexOfAnyExcept((byte)0) < 0)
        {
            return IsNonPublicV4(b[12..]);
        }

        return b[0] == 0x20 && b[1] == 0x02 && IsNonPublicV4(b[2..6]);
    }
}
