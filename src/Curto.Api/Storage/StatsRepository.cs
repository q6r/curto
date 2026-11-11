using Curto.Api.Models;
using Microsoft.Data.Sqlite;

namespace Curto.Api.Storage;

/// <summary>What to aggregate. All instants are Unix seconds; <see cref="PreviousFrom"/> &lt;= <see cref="From"/> &lt; <see cref="To"/>.</summary>
/// <param name="Slug">Restrict to one link, or null for all links.</param>
/// <param name="From">Inclusive start of the window.</param>
/// <param name="To">Exclusive end of the window.</param>
/// <param name="PreviousFrom">Inclusive start of the preceding window of equal length (it ends at <see cref="From"/>).</param>
/// <param name="BucketSeconds">Width of one series bucket.</param>
/// <param name="Now">Instant used to classify links as active or expired.</param>
internal sealed record StatsQuery(string? Slug, long From, long To, long PreviousFrom, long BucketSeconds, long Now);

/// <summary>Raw aggregates for one <see cref="StatsQuery"/>, read from a single snapshot.</summary>
/// <param name="Links">Link counts.</param>
/// <param name="Clicks">Clicks in the window.</param>
/// <param name="PreviousClicks">Clicks in the preceding window.</param>
/// <param name="Visitors">Unique daily visitors in the window.</param>
/// <param name="PreviousVisitors">Unique daily visitors in the preceding window.</param>
/// <param name="Series">Non-empty buckets as (index counted from <c>From</c>, clicks, visitors).</param>
/// <param name="TopLinks">Most clicked links.</param>
/// <param name="Referrers">Referrer hosts ranked by clicks.</param>
/// <param name="Devices">Device classes ranked by clicks.</param>
/// <param name="Browsers">Browser families ranked by clicks.</param>
/// <param name="Os">Operating-system families ranked by clicks.</param>
internal sealed record StatsData(
    LinkCounts Links,
    long Clicks,
    long PreviousClicks,
    long Visitors,
    long PreviousVisitors,
    IReadOnlyList<(long Bucket, long Clicks, long Visitors)> Series,
    IReadOnlyList<TopLink> TopLinks,
    IReadOnlyList<NamedCount> Referrers,
    IReadOnlyList<NamedCount> Devices,
    IReadOnlyList<NamedCount> Browsers,
    IReadOnlyList<NamedCount> Os);

/// <summary>
/// Aggregations over the click table. Statements are assembled only from constant fragments (the optional
/// per-link restriction is one of them); every value travels as a parameter.
/// </summary>
/// <param name="database">Connection source.</param>
internal sealed class StatsRepository(Database database)
{
    private const int RankedLimit = 10;

    private const string PerLinkFilter = " AND slug = $slug";
    private const string PerLinkFilterAliased = " AND c.slug = $slug";

    private const string LinkCountsSql = """
        SELECT COUNT(*),
               COALESCE(SUM(CASE WHEN (expires_at IS NULL OR expires_at > $now)
                                  AND (max_clicks IS NULL OR clicks < max_clicks) THEN 1 ELSE 0 END), 0)
        FROM links
        """;

    private const string TotalsSql = """
        SELECT COALESCE(SUM(CASE WHEN at >= $from THEN 1 ELSE 0 END), 0),
               COUNT(DISTINCT CASE WHEN at >= $from THEN visitor END),
               COALESCE(SUM(CASE WHEN at < $from THEN 1 ELSE 0 END), 0),
               COUNT(DISTINCT CASE WHEN at < $from THEN visitor END)
        FROM clicks
        WHERE at >= $previousFrom AND at < $to
        """;

    private const string SeriesSql = """
        SELECT (at - $from) / $bucket AS bucket, COUNT(*), COUNT(DISTINCT visitor)
        FROM clicks
        WHERE at >= $from AND at < $to
        """;

    private const string SeriesTail = " GROUP BY bucket ORDER BY bucket;";

    private const string TopLinksSql = """
        SELECT c.slug, l.title, l.url, COUNT(*) AS total
        FROM clicks c JOIN links l ON l.slug = c.slug
        WHERE c.at >= $from AND c.at < $to
        """;

    private const string TopLinksTail = " GROUP BY c.slug ORDER BY total DESC, c.slug ASC LIMIT $limit;";

    private const string ReferrersSql = "SELECT COALESCE(referrer, $direct) AS name, COUNT(*) AS total FROM clicks WHERE at >= $from AND at < $to";
    private const string DevicesSql = "SELECT device AS name, COUNT(*) AS total FROM clicks WHERE at >= $from AND at < $to";
    private const string BrowsersSql = "SELECT browser AS name, COUNT(*) AS total FROM clicks WHERE at >= $from AND at < $to";
    private const string OsSql = "SELECT os AS name, COUNT(*) AS total FROM clicks WHERE at >= $from AND at < $to";

    private const string RankedTail = " GROUP BY name ORDER BY total DESC, name ASC LIMIT $limit;";

    /// <summary>Runs every aggregation inside one read transaction, so all numbers come from the same snapshot.</summary>
    /// <param name="query">Window and scope.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async Task<StatsData> LoadAsync(StatsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var perLink = query.Slug is not null;
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: true);

        var links = await ReadLinkCountsAsync(connection, transaction, query, cancellationToken);
        var totals = await ReadTotalsAsync(connection, transaction, query, cancellationToken);
        var series = await ReadSeriesAsync(connection, transaction, query, cancellationToken);
        var top = await ReadTopLinksAsync(connection, transaction, query, cancellationToken);
        var referrers = await ReadRankedAsync(connection, transaction, Compose(ReferrersSql, perLink, PerLinkFilter, RankedTail), query, cancellationToken);
        var devices = await ReadRankedAsync(connection, transaction, Compose(DevicesSql, perLink, PerLinkFilter, RankedTail), query, cancellationToken);
        var browsers = await ReadRankedAsync(connection, transaction, Compose(BrowsersSql, perLink, PerLinkFilter, RankedTail), query, cancellationToken);
        var os = await ReadRankedAsync(connection, transaction, Compose(OsSql, perLink, PerLinkFilter, RankedTail), query, cancellationToken);

        return new StatsData(
            links,
            totals.Clicks,
            totals.PreviousClicks,
            totals.Visitors,
            totals.PreviousVisitors,
            series,
            top,
            referrers,
            devices,
            browsers,
            os);
    }

    private static string Compose(string head, bool perLink, string filter, string tail) =>
        head + (perLink ? filter : string.Empty) + tail;

    private static SqliteCommand Bind(SqliteCommand command, StatsQuery query)
    {
        command
            .With("$from", query.From)
            .With("$to", query.To)
            .With("$previousFrom", query.PreviousFrom)
            .With("$bucket", query.BucketSeconds)
            .With("$now", query.Now)
            .With("$direct", ClickEvent.DirectReferrer)
            .With("$limit", RankedLimit);
        if (query.Slug is not null)
        {
            command.With("$slug", query.Slug);
        }

        return command;
    }

    private static async Task<LinkCounts> ReadLinkCountsAsync(SqliteConnection connection, SqliteTransaction transaction, StatsQuery query, CancellationToken cancellationToken)
    {
        var sql = Compose(LinkCountsSql, query.Slug is not null, " WHERE slug = $slug", ";");
        await using var command = Bind(connection.CreateCommand(sql, transaction), query);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var total = checked((int)reader.GetInt64(0));
        var active = checked((int)reader.GetInt64(1));
        return new LinkCounts(total, active, total - active);
    }

    private static async Task<(long Clicks, long Visitors, long PreviousClicks, long PreviousVisitors)> ReadTotalsAsync(
        SqliteConnection connection, SqliteTransaction transaction, StatsQuery query, CancellationToken cancellationToken)
    {
        var sql = Compose(TotalsSql, query.Slug is not null, PerLinkFilter, ";");
        await using var command = Bind(connection.CreateCommand(sql, transaction), query);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
    }

    private static async Task<List<(long Bucket, long Clicks, long Visitors)>> ReadSeriesAsync(
        SqliteConnection connection, SqliteTransaction transaction, StatsQuery query, CancellationToken cancellationToken)
    {
        var sql = Compose(SeriesSql, query.Slug is not null, PerLinkFilter, SeriesTail);
        await using var command = Bind(connection.CreateCommand(sql, transaction), query);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        List<(long Bucket, long Clicks, long Visitors)> rows = [];
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2)));
        }

        return rows;
    }

    private static async Task<List<TopLink>> ReadTopLinksAsync(
        SqliteConnection connection, SqliteTransaction transaction, StatsQuery query, CancellationToken cancellationToken)
    {
        var sql = Compose(TopLinksSql, query.Slug is not null, PerLinkFilterAliased, TopLinksTail);
        await using var command = Bind(connection.CreateCommand(sql, transaction), query);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        List<TopLink> rows = [];
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new TopLink(reader.GetString(0), reader.GetNullableString(1), reader.GetString(2), reader.GetInt64(3)));
        }

        return rows;
    }

    private static async Task<List<NamedCount>> ReadRankedAsync(
        SqliteConnection connection, SqliteTransaction transaction, string sql, StatsQuery query, CancellationToken cancellationToken)
    {
        await using var command = Bind(connection.CreateCommand(sql, transaction), query);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        List<NamedCount> rows = [];
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new NamedCount(reader.GetString(0), reader.GetInt64(1)));
        }

        return rows;
    }
}
