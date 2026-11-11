namespace Curto.Api.Models;

/// <summary>Analytics for all links (overview) or for a single link; both share this shape.</summary>
/// <param name="Range">The window the numbers refer to.</param>
/// <param name="Links">Link counts at the time of the request.</param>
/// <param name="Clicks">Clicks in the window and in the window right before it.</param>
/// <param name="Visitors">Unique daily visitors in the window and in the window right before it.</param>
/// <param name="Series">One point per bucket, zero-filled.</param>
/// <param name="TopLinks">Most clicked links in the window.</param>
/// <param name="Referrers">Referrer hosts ranked by clicks.</param>
/// <param name="Devices">Device classes ranked by clicks.</param>
/// <param name="Browsers">Browser families ranked by clicks.</param>
/// <param name="Os">Operating-system families ranked by clicks.</param>
internal sealed record StatsResponse(
    StatsRange Range,
    LinkCounts Links,
    Comparison Clicks,
    Comparison Visitors,
    IReadOnlyList<SeriesPoint> Series,
    IReadOnlyList<TopLink> TopLinks,
    IReadOnlyList<NamedCount> Referrers,
    IReadOnlyList<NamedCount> Devices,
    IReadOnlyList<NamedCount> Browsers,
    IReadOnlyList<NamedCount> Os);

/// <summary>Bucket-aligned window of a statistics response.</summary>
/// <param name="From">Inclusive start (UTC).</param>
/// <param name="To">Exclusive end (UTC); the end of the bucket that contains "now".</param>
/// <param name="Bucket"><c>hour</c> or <c>day</c>.</param>
internal sealed record StatsRange(DateTimeOffset From, DateTimeOffset To, string Bucket);

/// <summary>Link counts. <c>Expired</c> includes links that exhausted their click budget.</summary>
/// <param name="Total">All links.</param>
/// <param name="Active">Links that still redirect.</param>
/// <param name="Expired">Links that no longer redirect (expired or exhausted).</param>
internal sealed record LinkCounts(int Total, int Active, int Expired);

/// <summary>A value for the requested window and for the window of equal length right before it.</summary>
/// <param name="InRange">Value inside the requested window.</param>
/// <param name="Previous">Value inside the preceding window.</param>
internal sealed record Comparison(long InRange, long Previous);

/// <summary>One bucket of the time series.</summary>
/// <param name="T">Bucket start (UTC).</param>
/// <param name="Clicks">Clicks inside the bucket.</param>
/// <param name="Visitors">Unique visitors inside the bucket.</param>
internal sealed record SeriesPoint(DateTimeOffset T, long Clicks, long Visitors);

/// <summary>A ranked link.</summary>
/// <param name="Slug">Link slug.</param>
/// <param name="Title">Link title, if any.</param>
/// <param name="Url">Link target.</param>
/// <param name="Clicks">Clicks inside the window.</param>
internal sealed record TopLink(string Slug, string? Title, string Url, long Clicks);

/// <summary>A ranked dimension value (referrer host, device, browser or operating system).</summary>
/// <param name="Name">The value.</param>
/// <param name="Clicks">Clicks inside the window.</param>
internal sealed record NamedCount(string Name, long Clicks);

/// <summary>Supported statistics windows.</summary>
internal enum StatsWindow
{
    /// <summary>Last 24 hourly buckets.</summary>
    Last24Hours,

    /// <summary>Last 7 daily buckets.</summary>
    Last7Days,

    /// <summary>Last 30 daily buckets.</summary>
    Last30Days,

    /// <summary>Last 90 daily buckets.</summary>
    Last90Days,
}
