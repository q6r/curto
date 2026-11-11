using Curto.Api.Models;
using Curto.Api.Storage;

namespace Curto.Api.Services;

/// <summary>
/// Turns raw aggregates into the API shape. Windows are aligned to whole buckets (UTC hours for 24h, UTC days otherwise)
/// and always end with the bucket that contains "now", so the series has a fixed number of points, the first bucket is
/// complete and the previous window is exactly as long as the requested one.
/// </summary>
/// <param name="stats">Aggregations.</param>
/// <param name="links">Used to check that a link exists before computing its statistics.</param>
/// <param name="time">Clock.</param>
internal sealed class StatsService(StatsRepository stats, LinkRepository links, TimeProvider time)
{
    private const long HourSeconds = 3600;
    private const long DaySeconds = 86_400;

    /// <summary>Parses <c>24h</c>, <c>7d</c>, <c>30d</c> or <c>90d</c> (default <c>7d</c> when null).</summary>
    /// <param name="text">Query-string value.</param>
    /// <param name="window">Parsed window.</param>
    public static bool TryParseWindow(string? text, out StatsWindow window)
    {
        switch (text)
        {
            case null or "7d":
                window = StatsWindow.Last7Days;
                return true;
            case "24h":
                window = StatsWindow.Last24Hours;
                return true;
            case "30d":
                window = StatsWindow.Last30Days;
                return true;
            case "90d":
                window = StatsWindow.Last90Days;
                return true;
            default:
                window = default;
                return false;
        }
    }

    /// <summary>Statistics across every link.</summary>
    /// <param name="window">Requested window.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public Task<StatsResponse> OverviewAsync(StatsWindow window, CancellationToken cancellationToken) =>
        BuildAsync(null, window, cancellationToken);

    /// <summary>Statistics of one link.</summary>
    /// <param name="slug">Link slug.</param>
    /// <param name="window">Requested window.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Null when the link does not exist.</returns>
    public async Task<StatsResponse?> ForLinkAsync(string slug, StatsWindow window, CancellationToken cancellationToken)
    {
        if (await links.FindAsync(slug, cancellationToken) is null)
        {
            return null;
        }

        return await BuildAsync(slug, window, cancellationToken);
    }

    private async Task<StatsResponse> BuildAsync(string? slug, StatsWindow window, CancellationToken cancellationToken)
    {
        var (bucketSeconds, bucketCount, bucketName) = Describe(window);

        var now = time.GetUtcNow().ToUnixTimeSeconds();
        var to = now - (now % bucketSeconds) + bucketSeconds;
        var from = to - (bucketCount * bucketSeconds);
        var previousFrom = from - (bucketCount * bucketSeconds);

        var data = await stats.LoadAsync(new StatsQuery(slug, from, to, previousFrom, bucketSeconds, now), cancellationToken);

        var filled = data.Series.ToDictionary(row => row.Bucket);
        var series = new SeriesPoint[bucketCount];
        for (var i = 0; i < bucketCount; i++)
        {
            filled.TryGetValue(i, out var row);
            series[i] = new SeriesPoint(UnixTime.FromDb(from + (i * bucketSeconds)), row.Clicks, row.Visitors);
        }

        return new StatsResponse(
            new StatsRange(UnixTime.FromDb(from), UnixTime.FromDb(to), bucketName),
            data.Links,
            new Comparison(data.Clicks, data.PreviousClicks),
            new Comparison(data.Visitors, data.PreviousVisitors),
            series,
            data.TopLinks,
            data.Referrers,
            data.Devices,
            data.Browsers,
            data.Os);
    }

    private static (long BucketSeconds, int Buckets, string Name) Describe(StatsWindow window) => window switch
    {
        StatsWindow.Last24Hours => (HourSeconds, 24, "hour"),
        StatsWindow.Last7Days => (DaySeconds, 7, "day"),
        StatsWindow.Last30Days => (DaySeconds, 30, "day"),
        StatsWindow.Last90Days => (DaySeconds, 90, "day"),
        _ => throw new ArgumentOutOfRangeException(nameof(window), window, "Unknown statistics window."),
    };
}
