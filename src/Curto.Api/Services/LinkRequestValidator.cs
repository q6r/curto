using System.Text.Json;
using Curto.Api.Infrastructure;
using Curto.Api.Models;

namespace Curto.Api.Services;

/// <summary>Result of validating a request: a value, or the per-field messages explaining why there is none.</summary>
/// <typeparam name="T">Validated value type.</typeparam>
/// <param name="Value">The validated value; null when there are errors.</param>
/// <param name="Errors">Messages keyed by JSON field name.</param>
internal sealed record Validated<T>(T? Value, IReadOnlyDictionary<string, string[]> Errors)
    where T : class
{
    /// <summary>True when validation passed.</summary>
    public bool IsValid => Errors.Count == 0;
}

/// <summary>Validates and normalises link create and patch requests.</summary>
/// <param name="urls">Target URL policy.</param>
/// <param name="time">Clock for the "expiry must be in the future" rule.</param>
internal sealed class LinkRequestValidator(TargetUrlValidator urls, TimeProvider time)
{
    /// <summary>Longest title.</summary>
    public const int MaxTitleLength = 200;

    /// <summary>Most tags per link.</summary>
    public const int MaxTags = 10;

    /// <summary>Longest tag.</summary>
    public const int MaxTagLength = 32;

    /// <summary>Longest free-text search.</summary>
    public const int MaxQueryLength = 200;

    /// <summary>Page size when <c>limit</c> is omitted.</summary>
    public const int DefaultPageSize = 50;

    /// <summary>Largest page size.</summary>
    public const int MaxPageSize = 200;

    private static readonly string[] PatchableFields = ["url", "title", "tags", "expiresAt", "maxClicks"];

    /// <summary>Validates the body of <c>POST /api/links</c>.</summary>
    /// <param name="request">Deserialised body.</param>
    public Validated<NewLink> ValidateCreate(CreateLinkRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = new ErrorBag();

        var url = CheckUrl(request.Url, errors);

        string? slug = null;
        if (request.Slug is not null)
        {
            slug = request.Slug;
            if (!SlugRules.IsValidFormat(slug))
            {
                errors.Add("slug", $"slug must be {SlugRules.MinLength}-{SlugRules.MaxLength} characters from A-Z, a-z, 0-9, '_' and '-'.");
            }
            else if (SlugRules.IsReserved(slug))
            {
                errors.Add("slug", $"slug '{slug}' is reserved.");
            }
        }

        var title = CheckTitle(request.Title, errors);
        var tags = CheckTags(request.Tags, errors);
        var expiresAt = CheckExpiry(request.ExpiresAt, errors);
        var maxClicks = CheckMaxClicks(request.MaxClicks, errors);

        return errors.Any
            ? new Validated<NewLink>(null, errors.ToDictionary())
            : new Validated<NewLink>(new NewLink(slug, url!, title, tags, expiresAt, maxClicks), errors.ToDictionary());
    }

    /// <summary>
    /// Validates a JSON merge-patch document (RFC 7396): present fields change, <c>null</c> clears an optional field,
    /// absent fields stay as they are. <c>slug</c> cannot be changed.
    /// </summary>
    /// <param name="body">The request body.</param>
    public Validated<LinkChanges> ValidatePatch(JsonElement body)
    {
        var errors = new ErrorBag();
        if (body.ValueKind != JsonValueKind.Object)
        {
            errors.Add("body", "The request body must be a JSON object.");
            return new Validated<LinkChanges>(null, errors.ToDictionary());
        }

        var url = Optional<string>.Unset;
        var title = Optional<string>.Unset;
        var tags = Optional<IReadOnlyList<string>>.Unset;
        var expiresAt = Optional<DateTimeOffset?>.Unset;
        var maxClicks = Optional<int?>.Unset;

        foreach (var property in body.EnumerateObject())
        {
            switch (property.Name)
            {
                case "url":
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        var checkedUrl = CheckUrl(property.Value.GetString(), errors);
                        if (checkedUrl is not null)
                        {
                            url = Optional<string>.Of(checkedUrl);
                        }
                    }
                    else
                    {
                        errors.Add("url", "url must be a string.");
                    }

                    break;

                case "title":
                    if (property.Value.ValueKind is JsonValueKind.Null or JsonValueKind.String)
                    {
                        title = Optional<string>.Of(CheckTitle(property.Value.GetString(), errors));
                    }
                    else
                    {
                        errors.Add("title", "title must be a string or null.");
                    }

                    break;

                case "tags":
                    tags = ReadTags(property.Value, errors);
                    break;

                case "expiresAt":
                    expiresAt = ReadExpiry(property.Value, errors);
                    break;

                case "maxClicks":
                    maxClicks = ReadMaxClicks(property.Value, errors);
                    break;

                case "slug":
                    errors.Add("slug", "slug cannot be changed.");
                    break;

                default:
                    errors.Add(property.Name, $"unknown property; patchable fields are {string.Join(", ", PatchableFields)}.");
                    break;
            }
        }

        return errors.Any
            ? new Validated<LinkChanges>(null, errors.ToDictionary())
            : new Validated<LinkChanges>(new LinkChanges(url, title, tags, expiresAt, maxClicks), errors.ToDictionary());
    }

    /// <summary>Validates the query string of <c>GET /api/links</c> and applies the defaults.</summary>
    /// <param name="parameters">Bound query-string values.</param>
    public static Validated<LinkQuery> ValidateQuery(LinkListParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        var errors = new ErrorBag();

        var text = parameters.Query?.Trim();
        if (text?.Length > MaxQueryLength)
        {
            errors.Add("query", $"query must be at most {MaxQueryLength} characters.");
        }

        var tag = parameters.Tag?.Trim().ToLowerInvariant();
        if (tag?.Length > MaxTagLength)
        {
            errors.Add("tag", $"tag must be at most {MaxTagLength} characters.");
        }

        LinkStatus? status = null;
        if (!string.IsNullOrEmpty(parameters.Status))
        {
            status = parameters.Status.ToLowerInvariant() switch
            {
                "active" => LinkStatus.Active,
                "expired" => LinkStatus.Expired,
                "exhausted" => LinkStatus.Exhausted,
                _ => null,
            };

            if (status is null)
            {
                errors.Add("status", "status must be one of: active, expired, exhausted.");
            }
        }

        var sort = LinkSort.CreatedAt;
        if (!string.IsNullOrEmpty(parameters.Sort))
        {
            if (string.Equals(parameters.Sort, "clicks", StringComparison.OrdinalIgnoreCase))
            {
                sort = LinkSort.Clicks;
            }
            else if (!string.Equals(parameters.Sort, "createdAt", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("sort", "sort must be one of: createdAt, clicks.");
            }
        }

        var descending = true;
        if (!string.IsNullOrEmpty(parameters.Dir))
        {
            if (string.Equals(parameters.Dir, "asc", StringComparison.OrdinalIgnoreCase))
            {
                descending = false;
            }
            else if (!string.Equals(parameters.Dir, "desc", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("dir", "dir must be one of: asc, desc.");
            }
        }

        var limit = parameters.Limit ?? DefaultPageSize;
        if (limit is < 1 or > MaxPageSize)
        {
            errors.Add("limit", $"limit must be between 1 and {MaxPageSize}.");
        }

        var offset = parameters.Offset ?? 0;
        if (offset < 0)
        {
            errors.Add("offset", "offset must not be negative.");
        }

        var query = new LinkQuery(
            string.IsNullOrEmpty(text) ? null : text,
            string.IsNullOrEmpty(tag) ? null : tag,
            status,
            sort,
            descending,
            limit,
            offset);

        return errors.Any
            ? new Validated<LinkQuery>(null, errors.ToDictionary())
            : new Validated<LinkQuery>(query, errors.ToDictionary());
    }

    private string? CheckUrl(string? raw, ErrorBag errors)
    {
        var check = urls.Check(raw);
        if (!check.IsValid)
        {
            errors.Add("url", check.Error!);
        }

        return check.Url;
    }

    private static string? CheckTitle(string? raw, ErrorBag errors)
    {
        var title = raw?.Trim();
        if (string.IsNullOrEmpty(title))
        {
            return null;
        }

        if (title.Length > MaxTitleLength)
        {
            errors.Add("title", $"title must be at most {MaxTitleLength} characters.");
        }
        else if (title.Any(char.IsControl))
        {
            errors.Add("title", "title must not contain control characters.");
        }

        return title;
    }

    private static List<string> CheckTags(IReadOnlyList<string>? raw, ErrorBag errors)
    {
        if (raw is null || raw.Count == 0)
        {
            return [];
        }

        if (raw.Count > MaxTags)
        {
            errors.Add("tags", $"at most {MaxTags} tags are allowed.");
            return [];
        }

        List<string> result = [];
        foreach (var item in raw)
        {
            var tag = item?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(tag) || tag.Length > MaxTagLength || !tag.All(IsTagCharacter))
            {
                errors.Add("tags", $"each tag must be 1-{MaxTagLength} letters, digits, '-', '_', '.' or ':'.");
                continue;
            }

            if (!result.Contains(tag, StringComparer.Ordinal))
            {
                result.Add(tag);
            }
        }

        return result;
    }

    private DateTimeOffset? CheckExpiry(DateTimeOffset? raw, ErrorBag errors)
    {
        if (raw is not { } value)
        {
            return null;
        }

        // Stored with one-second precision; normalise now so the response matches what a later GET returns.
        var truncated = DateTimeOffset.FromUnixTimeSeconds(value.ToUnixTimeSeconds());
        if (truncated <= time.GetUtcNow())
        {
            errors.Add("expiresAt", "expiresAt must be in the future.");
        }

        return truncated;
    }

    private static int? CheckMaxClicks(int? raw, ErrorBag errors)
    {
        if (raw is < 1)
        {
            errors.Add("maxClicks", "maxClicks must be at least 1.");
        }

        return raw;
    }

    private static Optional<IReadOnlyList<string>> ReadTags(JsonElement value, ErrorBag errors)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return Optional<IReadOnlyList<string>>.Of([]);
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            errors.Add("tags", "tags must be an array of strings or null.");
            return Optional<IReadOnlyList<string>>.Unset;
        }

        List<string> raw = [];
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                errors.Add("tags", "tags must be an array of strings or null.");
                return Optional<IReadOnlyList<string>>.Unset;
            }

            raw.Add(item.GetString()!);
        }

        return Optional<IReadOnlyList<string>>.Of(CheckTags(raw, errors));
    }

    private Optional<DateTimeOffset?> ReadExpiry(JsonElement value, ErrorBag errors)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return Optional<DateTimeOffset?>.Of(null);
        }

        if (!UtcDateTimeOffsetConverter.TryRead(value, out var parsed))
        {
            errors.Add("expiresAt", UtcDateTimeOffsetConverter.ExpectedFormat);
            return Optional<DateTimeOffset?>.Unset;
        }

        return Optional<DateTimeOffset?>.Of(CheckExpiry(parsed, errors));
    }

    private static Optional<int?> ReadMaxClicks(JsonElement value, ErrorBag errors)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return Optional<int?>.Of(null);
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var parsed))
        {
            errors.Add("maxClicks", "maxClicks must be an integer or null.");
            return Optional<int?>.Unset;
        }

        return Optional<int?>.Of(CheckMaxClicks(parsed, errors));
    }

    private static bool IsTagCharacter(char c) => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or ':';

    /// <summary>Collects messages per field, preserving the order in which they were found.</summary>
    private sealed class ErrorBag
    {
        private readonly Dictionary<string, List<string>> _errors = new(StringComparer.Ordinal);

        public bool Any => _errors.Count > 0;

        public void Add(string field, string message)
        {
            if (!_errors.TryGetValue(field, out var list))
            {
                _errors[field] = list = [];
            }

            if (!list.Contains(message, StringComparer.Ordinal))
            {
                list.Add(message);
            }
        }

        public Dictionary<string, string[]> ToDictionary() =>
            _errors.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal);
    }
}
