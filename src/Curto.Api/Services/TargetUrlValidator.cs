using System.Text;
using Curto.Api.Infrastructure;
using Microsoft.Extensions.Options;

namespace Curto.Api.Services;

/// <summary>Outcome of validating a target URL: either the normalised URL or an error message.</summary>
/// <param name="Url">Normalised absolute URL, or null when invalid.</param>
/// <param name="Error">What is wrong, or null when valid.</param>
internal readonly record struct UrlCheck(string? Url, string? Error)
{
    /// <summary>True when <see cref="Url"/> is usable.</summary>
    public bool IsValid => Url is not null;
}

/// <summary>
/// Accepts only absolute http(s) URLs of reasonable size without credentials, and normalises them. The normalised form
/// is pure ASCII (host lower-cased, other characters percent-encoded), which is what an HTTP <c>Location</c> header needs.
/// </summary>
/// <param name="options">Source of the private-target policy.</param>
internal sealed class TargetUrlValidator(IOptions<CurtoOptions> options)
{
    /// <summary>Longest accepted URL, before and after normalisation.</summary>
    public const int MaxLength = 2048;

    /// <summary>Validates and normalises a candidate target URL.</summary>
    /// <param name="input">Raw value from the request.</param>
    public UrlCheck Check(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return Fail("url is required.");
        }

        var text = input.Trim();
        if (text.Length > MaxLength)
        {
            return Fail($"url must be at most {MaxLength} characters.");
        }

        if (text.Any(char.IsControl))
        {
            return Fail("url must not contain control characters.");
        }

        if (!text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return Fail("url must be an absolute http or https URL.");
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Host.Length == 0)
        {
            return Fail("url is not a valid absolute http or https URL.");
        }

        if (uri.UserInfo.Length > 0)
        {
            return Fail("url must not contain credentials (user:password@).");
        }

        var normalised = Normalise(uri);
        if (!Ascii.IsValid(normalised))
        {
            // Unreachable for URIs the framework parsed, but the redirect depends on it: Kestrel throws when asked to write a
            // non-ASCII header value, so fail closed instead of storing something that would turn into a 500 later.
            return Fail("url could not be reduced to ASCII.");
        }

        if (normalised.Length > MaxLength)
        {
            return Fail($"url must be at most {MaxLength} characters once normalised.");
        }

        if (options.Value.BlockPrivateTargets && NonPublicHosts.Matches(uri))
        {
            return Fail("url must not point at localhost or a private network address.");
        }

        return new UrlCheck(normalised, null);
    }

    private static UrlCheck Fail(string error) => new(null, error);

    /// <summary>
    /// <see cref="Uri.AbsoluteUri"/> percent-encodes non-ASCII characters in the path, query and fragment, but leaves an
    /// internationalised host name as typed. Swap the host for its punycode form so the whole URL is ASCII.
    /// </summary>
    /// <param name="uri">Parsed URL without credentials.</param>
    private static string Normalise(Uri uri)
    {
        var absolute = uri.AbsoluteUri;
        if (Ascii.IsValid(uri.Host))
        {
            return absolute;
        }

        var hostStart = uri.Scheme.Length + "://".Length;
        return string.Concat(absolute.AsSpan(0, hostStart), uri.IdnHost, absolute.AsSpan(hostStart + uri.Host.Length));
    }
}
