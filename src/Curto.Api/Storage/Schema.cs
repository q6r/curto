namespace Curto.Api.Storage;

/// <summary>
/// Ordered, append-only schema migrations. Each script ends by bumping <c>PRAGMA user_version</c>, so the
/// database file itself records how far it got. Never edit a released script: add a new one.
/// </summary>
internal static class Schema
{
    /// <summary>Scripts in application order; script <c>n</c> moves the database from version <c>n</c> to <c>n + 1</c>.</summary>
    public static IReadOnlyList<string> Migrations { get; } = [V1];

    private const string V1 = """
        CREATE TABLE links (
            slug        TEXT    NOT NULL PRIMARY KEY,
            url         TEXT    NOT NULL,
            title       TEXT,
            tags        TEXT    NOT NULL DEFAULT '[]',
            created_at  INTEGER NOT NULL,
            expires_at  INTEGER,
            max_clicks  INTEGER,
            clicks      INTEGER NOT NULL DEFAULT 0
        ) WITHOUT ROWID;

        CREATE INDEX ix_links_created_at ON links (created_at);

        CREATE TABLE clicks (
            id        INTEGER PRIMARY KEY,
            slug      TEXT    NOT NULL REFERENCES links (slug) ON DELETE CASCADE,
            at        INTEGER NOT NULL,
            visitor   INTEGER NOT NULL,
            referrer  TEXT,
            browser   TEXT    NOT NULL,
            os        TEXT    NOT NULL,
            device    TEXT    NOT NULL
        );

        CREATE INDEX ix_clicks_at ON clicks (at);
        CREATE INDEX ix_clicks_slug_at ON clicks (slug, at);

        PRAGMA user_version = 1;
        """;
}
