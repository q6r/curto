using Curto.Api.Infrastructure;
using Curto.Api.Models;
using Microsoft.Extensions.Options;

namespace Curto.Api.Services;

/// <summary>Builds absolute short URLs and maps stored links to their API representation.</summary>
/// <param name="options">Source of the configured base URL.</param>
/// <param name="time">Clock used to derive the status.</param>
internal sealed class LinkMapper(IOptions<CurtoOptions> options, TimeProvider time)
{
    /// <summary>
    /// Short URL for a slug. Uses <c>CURTO_BASE_URL</c> when set, otherwise the origin of the current request
    /// (which depends on the Host header: configure the base URL for public deployments).
    /// </summary>
    /// <param name="request">Current request.</param>
    /// <param name="slug">Link slug.</param>
    public string ShortUrl(HttpRequest request, string slug)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Origin(request) + "/" + slug;
    }

    /// <summary>True when the base URL is configured, so URLs do not depend on request headers.</summary>
    public bool HasConfiguredBaseUrl => !string.IsNullOrEmpty(options.Value.BaseUrl);

    /// <summary>Maps a stored link to the API shape.</summary>
    /// <param name="link">Stored link.</param>
    /// <param name="request">Current request.</param>
    public LinkResponse ToResponse(Link link, HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(link);

        return new LinkResponse(
            link.Slug,
            ShortUrl(request, link.Slug),
            link.Url,
            link.Title,
            link.Tags,
            link.CreatedAt,
            link.ExpiresAt,
            link.MaxClicks,
            link.Clicks,
            link.StatusAt(time.GetUtcNow()));
    }

    private string Origin(HttpRequest request) =>
        options.Value.BaseUrl is { Length: > 0 } baseUrl
            ? baseUrl.TrimEnd('/')
            : $"{request.Scheme}://{request.Host}{request.PathBase}";
}
