namespace Curto.Api.Services;

/// <summary>Coarse client classification derived from a User-Agent string.</summary>
/// <param name="Browser">Browser family, <c>Bot</c> for crawlers and scripted clients, or <c>Other</c>.</param>
/// <param name="Os">Operating-system family or <c>Other</c>.</param>
/// <param name="Device">One of <c>desktop</c>, <c>mobile</c>, <c>tablet</c>, <c>bot</c>, <c>unknown</c>.</param>
internal readonly record struct ClientInfo(string Browser, string Os, string Device);

/// <summary>
/// A small, allocation-light User-Agent classifier. It recognises browser, OS and device <em>families</em> only: the raw
/// string is never stored. Ordering matters because most browsers imitate each other ("Chrome" appears in the Edge and
/// Opera strings, "Safari" in Chrome's, "Mac OS X" in iPhone's), so the most specific tokens are tested first.
/// Known limit: iPadOS 13+ Safari reports a Macintosh UA and is classified as desktop macOS.
/// </summary>
internal static class UserAgentParser
{
    /// <summary>User-Agent strings longer than this are truncated before inspection.</summary>
    public const int MaxInspectedLength = 512;

    /// <summary>Device class values.</summary>
    public static class Devices
    {
        /// <summary>Desktop or laptop.</summary>
        public const string Desktop = "desktop";

        /// <summary>Phone.</summary>
        public const string Mobile = "mobile";

        /// <summary>Tablet.</summary>
        public const string Tablet = "tablet";

        /// <summary>Crawler, link-preview fetcher, monitor or scripted client.</summary>
        public const string Bot = "bot";

        /// <summary>Missing or unrecognisable User-Agent.</summary>
        public const string Unknown = "unknown";
    }

    private static readonly string[] BotTokens =
    [
        "bot", "spider", "crawl", "slurp", "facebookexternalhit", "preview", "headlesschrome", "lighthouse",
        "monitor", "uptime", "pingdom", "embedly", "curl/", "wget/", "python-requests", "python-urllib",
        "go-http-client", "libwww", "httpclient", "java/", "node-fetch", "axios/", "postmanruntime", "scrapy",
    ];

    /// <summary>Classifies a User-Agent header value.</summary>
    /// <param name="userAgent">Raw header value; may be null or empty.</param>
    public static ClientInfo Parse(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return new ClientInfo("Other", "Other", Devices.Unknown);
        }

        var ua = userAgent.AsSpan(0, Math.Min(userAgent.Length, MaxInspectedLength));
        var os = DetectOs(ua);

        if (IsBot(ua))
        {
            return new ClientInfo("Bot", os, Devices.Bot);
        }

        return new ClientInfo(DetectBrowser(ua, os), os, DetectDevice(ua, os));
    }

    private static bool IsBot(ReadOnlySpan<char> ua)
    {
        foreach (var token in BotTokens)
        {
            if (ua.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string DetectOs(ReadOnlySpan<char> ua)
    {
        if (Has(ua, "Windows"))
        {
            return "Windows";
        }

        // iOS and Android must precede macOS and Linux: their strings contain "like Mac OS X" and "Linux".
        if (Has(ua, "iPhone") || Has(ua, "iPad") || Has(ua, "iPod"))
        {
            return "iOS";
        }

        if (Has(ua, "Android"))
        {
            return "Android";
        }

        if (Has(ua, "CrOS"))
        {
            return "ChromeOS";
        }

        if (Has(ua, "Macintosh") || Has(ua, "Mac OS X"))
        {
            return "macOS";
        }

        return Has(ua, "Linux") || Has(ua, "X11") ? "Linux" : "Other";
    }

    private static string DetectBrowser(ReadOnlySpan<char> ua, string os)
    {
        // Chromium derivatives first: they all carry a "Chrome/" token.
        if (Has(ua, "Edg/") || Has(ua, "EdgA/") || Has(ua, "EdgiOS/") || Has(ua, "Edge/"))
        {
            return "Edge";
        }

        if (Has(ua, "OPR/") || Has(ua, "OPiOS/") || Has(ua, "Opera"))
        {
            return "Opera";
        }

        if (Has(ua, "SamsungBrowser/"))
        {
            return "Samsung Internet";
        }

        if (Has(ua, "Firefox/") || Has(ua, "FxiOS/"))
        {
            return "Firefox";
        }

        if (Has(ua, "MSIE ") || Has(ua, "Trident/"))
        {
            return "Internet Explorer";
        }

        if (Has(ua, "Chrome/") || Has(ua, "CriOS/") || Has(ua, "Chromium/"))
        {
            return "Chrome";
        }

        // Safari proper, and iOS web views (WebKit without a "Safari/" token).
        if (Has(ua, "Safari/") || (os == "iOS" && Has(ua, "AppleWebKit")))
        {
            return "Safari";
        }

        return "Other";
    }

    private static string DetectDevice(ReadOnlySpan<char> ua, string os)
    {
        if (Has(ua, "iPad") || Has(ua, "Tablet") || Has(ua, "Kindle") || Has(ua, "Silk/") || Has(ua, "PlayBook"))
        {
            return Devices.Tablet;
        }

        if (Has(ua, "iPhone") || Has(ua, "iPod") || Has(ua, "Mobi") || Has(ua, "Windows Phone") || Has(ua, "Opera Mini")
            || Has(ua, "BlackBerry"))
        {
            return Devices.Mobile;
        }

        // Android phones say "Mobile"; an Android device without that marker is a tablet.
        if (os == "Android")
        {
            return Devices.Tablet;
        }

        return os is "Windows" or "macOS" or "Linux" or "ChromeOS" ? Devices.Desktop : Devices.Unknown;
    }

    private static bool Has(ReadOnlySpan<char> ua, string token) => ua.Contains(token, StringComparison.Ordinal);
}
