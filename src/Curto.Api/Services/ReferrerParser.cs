namespace Curto.Api.Services;

/// <summary>Reduces a <c>Referer</c> header to the referring host, discarding path, query, fragment, port and credentials.</summary>
internal static class ReferrerParser
{
    private const int MaxInputLength = 4096;
    private const int MaxHostLength = 253;

    /// <summary>Extracts the referrer host.</summary>
    /// <param name="referer">Raw header value.</param>
    /// <returns>Lower-case host without a leading <c>www.</c>, or null when the header is absent or unusable.</returns>
    public static string? HostOf(string? referer)
    {
        if (string.IsNullOrWhiteSpace(referer) || referer.Length > MaxInputLength)
        {
            return null;
        }

        if (!Uri.TryCreate(referer, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        var host = uri.IdnHost;
        if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) && host.Length > 4)
        {
            host = host[4..];
        }

        return host.Length is 0 or > MaxHostLength ? null : host.ToLowerInvariant();
    }
}
