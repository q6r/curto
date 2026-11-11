using System.Text.Json;
using Curto.Api.Models;
using Microsoft.Data.Sqlite;

namespace Curto.Api.Storage;

/// <summary>Persistence of short links. Every statement is a constant, parameterised SQL string.</summary>
/// <param name="database">Connection source.</param>
internal sealed class LinkRepository(Database database)
{
    private const string Columns = "slug, url, title, tags, created_at, expires_at, max_clicks, clicks";

    private const string InsertSql = """
        INSERT INTO links (slug, url, title, tags, created_at, expires_at, max_clicks, clicks)
        VALUES ($slug, $url, $title, $tags, $createdAt, $expiresAt, $maxClicks, 0)
        ON CONFLICT (slug) DO NOTHING;
        """;

    private const string FindSql = "SELECT " + Columns + " FROM links WHERE slug = $slug;";

    private const string UpdateSql = """
        UPDATE links
        SET url = $url, title = $title, tags = $tags, expires_at = $expiresAt, max_clicks = $maxClicks
        WHERE slug = $slug;
        """;

    private const string DeleteSql = "DELETE FROM links WHERE slug = $slug;";

    // Atomically spends one click of a limited link; no row is touched once the link is expired or exhausted.
    private const string ConsumeClickSql = """
        UPDATE links SET clicks = clicks + 1
        WHERE slug = $slug
          AND max_clicks IS NOT NULL
          AND clicks < max_clicks
          AND (expires_at IS NULL OR expires_at > $now);
        """;

    // NULL parameters switch a filter off. Status is derived exactly like Link.StatusAt.
    private const string Filter = """

        WHERE ($text IS NULL OR slug LIKE $text ESCAPE '\' OR IFNULL(title, '') LIKE $text ESCAPE '\' OR url LIKE $text ESCAPE '\')
          AND ($tag IS NULL OR EXISTS (SELECT 1 FROM json_each(links.tags) WHERE json_each.value = $tag))
          AND ($status IS NULL OR $status = CASE
                WHEN expires_at IS NOT NULL AND expires_at <= $now THEN 'expired'
                WHEN max_clicks IS NOT NULL AND clicks >= max_clicks THEN 'exhausted'
                ELSE 'active' END)
        """;

    private const string CountSql = "SELECT COUNT(*) FROM links" + Filter + ";";

    private const string Page = " LIMIT $limit OFFSET $offset;";

    private const string ListCreatedDesc = "SELECT " + Columns + " FROM links" + Filter + " ORDER BY created_at DESC, slug ASC" + Page;
    private const string ListCreatedAsc = "SELECT " + Columns + " FROM links" + Filter + " ORDER BY created_at ASC, slug ASC" + Page;
    private const string ListClicksDesc = "SELECT " + Columns + " FROM links" + Filter + " ORDER BY clicks DESC, slug ASC" + Page;
    private const string ListClicksAsc = "SELECT " + Columns + " FROM links" + Filter + " ORDER BY clicks ASC, slug ASC" + Page;

    /// <summary>Inserts a link unless the slug exists.</summary>
    /// <param name="link">Link to store; <see cref="Link.Clicks"/> is ignored (new links start at zero).</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>False when the slug is already taken.</returns>
    public async Task<bool> TryInsertAsync(Link link, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(link);

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand(InsertSql)
            .With("$slug", link.Slug)
            .With("$url", link.Url)
            .With("$title", link.Title)
            .With("$tags", SerializeTags(link.Tags))
            .With("$createdAt", UnixTime.ToDb(link.CreatedAt))
            .With("$expiresAt", UnixTime.ToDb(link.ExpiresAt))
            .With("$maxClicks", link.MaxClicks);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <summary>Loads a link by slug.</summary>
    /// <param name="slug">Case-sensitive slug.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async Task<Link?> FindAsync(string slug, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        return await FindAsync(connection, null, slug, cancellationToken);
    }

    /// <summary>Lists links matching the query and the unpaged total.</summary>
    /// <param name="query">Filters, sort and paging.</param>
    /// <param name="now">Instant used to derive link status.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async Task<(IReadOnlyList<Link> Items, int Total)> ListAsync(LinkQuery query, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var sql = (query.Sort, query.Descending) switch
        {
            (LinkSort.Clicks, true) => ListClicksDesc,
            (LinkSort.Clicks, false) => ListClicksAsc,
            (_, true) => ListCreatedDesc,
            _ => ListCreatedAsc,
        };

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: true);

        int total;
        await using (var count = BindFilter(connection.CreateCommand(CountSql, transaction), query, now))
        {
            total = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        }

        List<Link> items = [];
        await using (var list = BindFilter(connection.CreateCommand(sql, transaction), query, now)
            .With("$limit", query.Limit)
            .With("$offset", query.Offset))
        {
            await using var reader = await list.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(Read(reader));
            }
        }

        return (items, total);
    }

    /// <summary>Applies a merge patch inside one write transaction.</summary>
    /// <param name="slug">Link to change.</param>
    /// <param name="changes">Validated patch.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The updated link, or null when the slug does not exist.</returns>
    public async Task<Link?> UpdateAsync(string slug, LinkChanges changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();

        var current = await FindAsync(connection, transaction, slug, cancellationToken);
        if (current is null)
        {
            return null;
        }

        var updated = current with
        {
            Url = changes.Url.IsSet ? changes.Url.Value! : current.Url,
            Title = changes.Title.IsSet ? changes.Title.Value : current.Title,
            Tags = changes.Tags.IsSet ? changes.Tags.Value! : current.Tags,
            ExpiresAt = changes.ExpiresAt.IsSet ? changes.ExpiresAt.Value : current.ExpiresAt,
            MaxClicks = changes.MaxClicks.IsSet ? changes.MaxClicks.Value : current.MaxClicks,
        };

        await using (var command = connection.CreateCommand(UpdateSql, transaction)
            .With("$slug", slug)
            .With("$url", updated.Url)
            .With("$title", updated.Title)
            .With("$tags", SerializeTags(updated.Tags))
            .With("$expiresAt", UnixTime.ToDb(updated.ExpiresAt))
            .With("$maxClicks", updated.MaxClicks))
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return updated;
    }

    /// <summary>Deletes a link; its click rows go with it (foreign-key cascade).</summary>
    /// <param name="slug">Link to delete.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>False when the slug does not exist.</returns>
    public async Task<bool> DeleteAsync(string slug, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand(DeleteSql).With("$slug", slug);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <summary>Spends one click of a link that has a click budget, if the link is still usable.</summary>
    /// <param name="slug">Link to charge.</param>
    /// <param name="now">Instant used for the expiry check.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>False when the budget is spent or the link expired (or has no budget).</returns>
    public async Task<bool> TryConsumeClickAsync(string slug, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand(ConsumeClickSql)
            .With("$slug", slug)
            .With("$now", UnixTime.ToDb(now));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static async Task<Link?> FindAsync(SqliteConnection connection, SqliteTransaction? transaction, string slug, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(FindSql, transaction).With("$slug", slug);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    private static SqliteCommand BindFilter(SqliteCommand command, LinkQuery query, DateTimeOffset now) => command
        .With("$text", query.Text is null ? null : "%" + EscapeLike(query.Text) + "%")
        .With("$tag", query.Tag)
        .With("$status", query.Status?.ToString().ToLowerInvariant())
        .With("$now", UnixTime.ToDb(now));

    private static Link Read(SqliteDataReader reader) => new(
        Slug: reader.GetString(0),
        Url: reader.GetString(1),
        Title: reader.GetNullableString(2),
        Tags: DeserializeTags(reader.GetString(3)),
        CreatedAt: UnixTime.FromDb(reader.GetInt64(4)),
        ExpiresAt: UnixTime.FromDb(reader.GetNullableInt64(5)),
        MaxClicks: reader.GetNullableInt64(6) is { } max ? checked((int)max) : null,
        Clicks: reader.GetInt64(7));

    private static string SerializeTags(IReadOnlyList<string> tags) => JsonSerializer.Serialize(tags);

    private static string[] DeserializeTags(string json) => JsonSerializer.Deserialize<string[]>(json) ?? [];

    /// <summary>Escapes LIKE wildcards so user text is matched literally (the SQL declares <c>ESCAPE '\'</c>).</summary>
    /// <param name="text">Raw user text.</param>
    internal static string EscapeLike(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}
