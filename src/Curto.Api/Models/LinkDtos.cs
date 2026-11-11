using System.Text.Json.Serialization;

namespace Curto.Api.Models;

/// <summary>Body of <c>POST /api/links</c>. Unknown properties are rejected so typos do not pass silently.</summary>
/// <param name="Url">Target URL (required).</param>
/// <param name="Slug">Optional custom slug, <c>[A-Za-z0-9_-]{3,32}</c>.</param>
/// <param name="Title">Optional label, at most 200 characters.</param>
/// <param name="Tags">Optional labels (at most 10).</param>
/// <param name="ExpiresAt">Optional expiry; must be in the future and carry a UTC offset.</param>
/// <param name="MaxClicks">Optional click budget (at least 1).</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CreateLinkRequest(
    string? Url,
    string? Slug,
    string? Title,
    IReadOnlyList<string>? Tags,
    DateTimeOffset? ExpiresAt,
    int? MaxClicks);

/// <summary>
/// Body of <c>PATCH /api/links/{slug}</c> (JSON merge patch, RFC 7396). Present fields change, <c>null</c> clears an
/// optional field, absent fields stay as they are. Declared for the API reference only: the endpoint reads the raw JSON
/// so that "absent" and "null" can be told apart.
/// </summary>
/// <param name="Url">New target URL (cannot be null).</param>
/// <param name="Title">New title, or null to clear.</param>
/// <param name="Tags">New tag list, or null to clear.</param>
/// <param name="ExpiresAt">New expiry, or null to remove it.</param>
/// <param name="MaxClicks">New click budget, or null to remove it.</param>
internal sealed record LinkPatchRequest(
    string? Url,
    string? Title,
    IReadOnlyList<string>? Tags,
    DateTimeOffset? ExpiresAt,
    int? MaxClicks);

/// <summary>A link as exposed by the API.</summary>
/// <param name="Slug">Public identifier.</param>
/// <param name="ShortUrl">Absolute short URL (base URL + slug).</param>
/// <param name="Url">Target URL.</param>
/// <param name="Title">Optional label.</param>
/// <param name="Tags">Labels.</param>
/// <param name="CreatedAt">Creation instant (UTC).</param>
/// <param name="ExpiresAt">Optional expiry.</param>
/// <param name="MaxClicks">Optional click budget.</param>
/// <param name="Clicks">Lifetime redirect counter.</param>
/// <param name="Status"><c>active</c>, <c>expired</c> or <c>exhausted</c>.</param>
internal sealed record LinkResponse(
    string Slug,
    string ShortUrl,
    string Url,
    string? Title,
    IReadOnlyList<string> Tags,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    int? MaxClicks,
    long Clicks,
    LinkStatus Status);

/// <summary>One page of <see cref="LinkResponse"/> items.</summary>
/// <param name="Items">The page.</param>
/// <param name="Total">Number of links matching the filters, ignoring paging.</param>
internal sealed record LinkPage(IReadOnlyList<LinkResponse> Items, int Total);

/// <summary>Query-string parameters of <c>GET /api/links</c>.</summary>
/// <param name="Query">Case-insensitive text matched against slug, title and URL.</param>
/// <param name="Tag">Exact tag filter.</param>
/// <param name="Status"><c>active</c>, <c>expired</c> or <c>exhausted</c>.</param>
/// <param name="Sort"><c>createdAt</c> (default) or <c>clicks</c>.</param>
/// <param name="Dir"><c>desc</c> (default) or <c>asc</c>.</param>
/// <param name="Limit">Page size, 1-200 (default 50).</param>
/// <param name="Offset">Items to skip (default 0).</param>
internal sealed record LinkListParameters(
    string? Query,
    string? Tag,
    string? Status,
    string? Sort,
    string? Dir,
    int? Limit,
    int? Offset);
