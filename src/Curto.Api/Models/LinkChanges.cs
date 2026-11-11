namespace Curto.Api.Models;

/// <summary>
/// A field in a merge-patch document: absent (<see cref="IsSet"/> false), set to a value, or set to
/// <c>null</c> (which clears the stored value).
/// </summary>
/// <typeparam name="T">Field type.</typeparam>
/// <param name="Value">The new value; meaningful only when <paramref name="IsSet"/> is true.</param>
/// <param name="IsSet">True when the property was present in the patch.</param>
internal readonly record struct Optional<T>(T? Value, bool IsSet)
{
    /// <summary>A field that was not part of the patch.</summary>
    public static Optional<T> Unset => default;

    /// <summary>A field that was part of the patch with the given value (possibly null).</summary>
    public static Optional<T> Of(T? value) => new(value, true);
}

/// <summary>A validated <c>PATCH /api/links/{slug}</c> document.</summary>
/// <param name="Url">New target URL.</param>
/// <param name="Title">New title, or null to clear.</param>
/// <param name="Tags">New tag list (replaces the old one).</param>
/// <param name="ExpiresAt">New expiry, or null to clear.</param>
/// <param name="MaxClicks">New click budget, or null to clear.</param>
internal sealed record LinkChanges(
    Optional<string> Url,
    Optional<string> Title,
    Optional<IReadOnlyList<string>> Tags,
    Optional<DateTimeOffset?> ExpiresAt,
    Optional<int?> MaxClicks);

/// <summary>A validated request to create a link.</summary>
/// <param name="Slug">Custom slug, or null to generate one.</param>
/// <param name="Url">Normalised target URL.</param>
/// <param name="Title">Optional label.</param>
/// <param name="Tags">Normalised tags.</param>
/// <param name="ExpiresAt">Optional expiry.</param>
/// <param name="MaxClicks">Optional click budget.</param>
internal sealed record NewLink(
    string? Slug,
    string Url,
    string? Title,
    IReadOnlyList<string> Tags,
    DateTimeOffset? ExpiresAt,
    int? MaxClicks);

/// <summary>Sort column for link listings.</summary>
internal enum LinkSort
{
    /// <summary>Creation time.</summary>
    CreatedAt,

    /// <summary>Lifetime click counter.</summary>
    Clicks,
}

/// <summary>A validated link listing query.</summary>
/// <param name="Text">Substring filter over slug, title and URL.</param>
/// <param name="Tag">Exact tag filter.</param>
/// <param name="Status">Status filter.</param>
/// <param name="Sort">Sort column.</param>
/// <param name="Descending">Sort direction.</param>
/// <param name="Limit">Page size.</param>
/// <param name="Offset">Rows to skip.</param>
internal sealed record LinkQuery(
    string? Text,
    string? Tag,
    LinkStatus? Status,
    LinkSort Sort,
    bool Descending,
    int Limit,
    int Offset);
