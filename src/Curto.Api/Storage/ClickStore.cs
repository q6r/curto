using Curto.Api.Models;
using Microsoft.Data.Sqlite;

namespace Curto.Api.Storage;

/// <summary>Bulk persistence of click events and retention pruning.</summary>
/// <param name="database">Connection source.</param>
internal sealed class ClickStore(Database database)
{
    // The EXISTS guard drops events whose link was deleted while they sat in the queue; a plain INSERT would
    // violate the foreign key and fail the whole batch.
    private const string InsertSql = """
        INSERT INTO clicks (slug, at, visitor, referrer, browser, os, device)
        SELECT $slug, $at, $visitor, $referrer, $browser, $os, $device
        WHERE EXISTS (SELECT 1 FROM links WHERE slug = $slug);
        """;

    private const string CounterSql = "UPDATE links SET clicks = clicks + $count WHERE slug = $slug;";

    private const string PruneSql = """
        DELETE FROM clicks
        WHERE id IN (SELECT id FROM clicks WHERE at < $cutoff LIMIT $limit);
        """;

    /// <summary>
    /// Writes a batch in one transaction: one row per event, plus one counter update per link for the events
    /// whose counter was not already advanced at redirect time.
    /// </summary>
    /// <param name="batch">Events to persist.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public async Task InsertBatchAsync(IReadOnlyList<ClickEvent> batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);

        if (batch.Count == 0)
        {
            return;
        }

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();

        await using (var insert = connection.CreateCommand(InsertSql, transaction))
        {
            var slug = insert.Parameters.Add("$slug", SqliteType.Text);
            var at = insert.Parameters.Add("$at", SqliteType.Integer);
            var visitor = insert.Parameters.Add("$visitor", SqliteType.Integer);
            var referrer = insert.Parameters.Add("$referrer", SqliteType.Text);
            var browser = insert.Parameters.Add("$browser", SqliteType.Text);
            var os = insert.Parameters.Add("$os", SqliteType.Text);
            var device = insert.Parameters.Add("$device", SqliteType.Text);

            foreach (var click in batch)
            {
                slug.Value = click.Slug;
                at.Value = UnixTime.ToDb(click.At);
                visitor.Value = click.Visitor;
                referrer.Value = (object?)click.Referrer ?? DBNull.Value;
                browser.Value = click.Browser;
                os.Value = click.Os;
                device.Value = click.Device;
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        var counts = batch
            .Where(click => !click.Counted)
            .GroupBy(click => click.Slug, StringComparer.Ordinal)
            .Select(group => (Slug: group.Key, Count: group.LongCount()));

        await using (var counter = connection.CreateCommand(CounterSql, transaction))
        {
            var slug = counter.Parameters.Add("$slug", SqliteType.Text);
            var count = counter.Parameters.Add("$count", SqliteType.Integer);
            foreach (var (key, value) in counts)
            {
                slug.Value = key;
                count.Value = value;
                await counter.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Deletes click rows older than <paramref name="cutoff"/>, in small chunks so writers are never blocked for long.</summary>
    /// <param name="cutoff">Rows strictly older than this instant are removed.</param>
    /// <param name="cancellationToken">Cancels the pruning between chunks.</param>
    /// <returns>Number of deleted rows.</returns>
    public async Task<long> PruneAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        const int chunk = 5_000;
        long total = 0;

        await using var connection = await database.OpenAsync(cancellationToken);
        int deleted;
        do
        {
            await using var command = connection.CreateCommand(PruneSql)
                .With("$cutoff", UnixTime.ToDb(cutoff))
                .With("$limit", chunk);
            deleted = await command.ExecuteNonQueryAsync(cancellationToken);
            total += deleted;
        }
        while (deleted == chunk);

        return total;
    }
}
