using System.Collections.Frozen;
using System.Security.Cryptography;

namespace Curto.Api.Services;

/// <summary>Source of fresh slugs. An interface so tests can force collisions.</summary>
internal interface ISlugGenerator
{
    /// <summary>Returns a new random slug.</summary>
    string Next();
}

/// <summary>Generates 7-character base62 slugs from the operating system's cryptographic random source.</summary>
internal sealed class SlugGenerator : ISlugGenerator
{
    /// <inheritdoc />
    public string Next()
    {
        // GetString picks every character uniformly (no modulo bias). Reserved words are re-rolled by the caller.
        return RandomNumberGenerator.GetString(SlugRules.Base62, SlugRules.GeneratedLength);
    }
}

/// <summary>Format and reserved-word rules for slugs.</summary>
internal static class SlugRules
{
    /// <summary>Shortest custom slug.</summary>
    public const int MinLength = 3;

    /// <summary>Longest custom slug.</summary>
    public const int MaxLength = 32;

    /// <summary>Length of generated slugs.</summary>
    public const int GeneratedLength = 7;

    /// <summary>Alphabet of generated slugs.</summary>
    public const string Base62 = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    // Routing is case-insensitive for literal segments, so "Docs" would be unreachable behind "/docs":
    // the deny list is compared ignoring case.
    private static readonly FrozenSet<string> Reserved = new[]
    {
        "admin", "api", "app", "assets", "auth", "curto", "dashboard", "docs", "events", "favicon", "health",
        "healthz", "index", "link", "links", "login", "logout", "metrics", "openapi", "qr", "readyz", "robots",
        "scalar", "settings", "sitemap", "static", "stats", "status", "swagger", "www",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> ReservedLookup =
        Reserved.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>The slugs no link may use.</summary>
    public static IReadOnlyCollection<string> ReservedWords => Reserved;

    /// <summary>True for 3-32 characters from <c>[A-Za-z0-9_-]</c>.</summary>
    /// <param name="slug">Candidate slug.</param>
    public static bool IsValidFormat(ReadOnlySpan<char> slug)
    {
        if (slug.Length is < MinLength or > MaxLength)
        {
            return false;
        }

        foreach (var c in slug)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>True when the slug collides with a route or a well-known file name (ignoring case).</summary>
    /// <param name="slug">Candidate slug.</param>
    public static bool IsReserved(ReadOnlySpan<char> slug) => ReservedLookup.Contains(slug);
}

/// <summary>Route constraint <c>{slug:slug}</c>: only well-formed slugs reach the handlers; junk paths 404 without a database hit.</summary>
internal sealed class SlugRouteConstraint : IRouteConstraint
{
    /// <inheritdoc />
    public bool Match(HttpContext? httpContext, IRouter? route, string routeKey, RouteValueDictionary values, RouteDirection routeDirection)
    {
        ArgumentNullException.ThrowIfNull(values);

        return values.TryGetValue(routeKey, out var value)
            && value is string text
            && SlugRules.IsValidFormat(text);
    }
}
