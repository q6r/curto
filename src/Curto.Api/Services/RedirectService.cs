using Curto.Api.Infrastructure;
using Curto.Api.Models;
using Curto.Api.Storage;

namespace Curto.Api.Services;

/// <summary>What a slug resolves to.</summary>
internal enum RedirectKind
{
    /// <summary>Redirect to the target.</summary>
    Found,

    /// <summary>No such link.</summary>
    NotFound,

    /// <summary>The link passed its expiry.</summary>
    Expired,

    /// <summary>The link spent its click budget.</summary>
    Exhausted,
}

/// <summary>Result of resolving a slug.</summary>
/// <param name="Kind">Outcome.</param>
/// <param name="Url">Target URL when <paramref name="Kind"/> is <see cref="RedirectKind.Found"/>.</param>
internal readonly record struct RedirectOutcome(RedirectKind Kind, string? Url = null);

/// <summary>
/// The redirect use case. The hot path does one indexed read plus a non-blocking channel write: the click is analysed
/// in memory (no raw IP or User-Agent leaves this method) and persisted later by <see cref="ClickWriterService"/>.
/// Links with a click budget are the exception: their counter is advanced atomically here so the limit is exact.
/// </summary>
/// <param name="links">Link persistence.</param>
/// <param name="clicks">Hand-off to the writer.</param>
/// <param name="visitors">Daily-salted visitor hashing.</param>
/// <param name="time">Clock.</param>
internal sealed class RedirectService(LinkRepository links, ClickChannel clicks, VisitorHasher visitors, TimeProvider time)
{
    /// <summary>Resolves a slug and, for real visits, records the click.</summary>
    /// <param name="slug">Case-sensitive slug.</param>
    /// <param name="http">Current request; only transient properties (address, User-Agent, Referer) are read.</param>
    /// <param name="recordClick">False for HEAD requests, which are link checks and not visits.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    public async Task<RedirectOutcome> ResolveAsync(string slug, HttpContext http, bool recordClick, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);

        var link = await links.FindAsync(slug, cancellationToken);
        if (link is null)
        {
            return new RedirectOutcome(RedirectKind.NotFound);
        }

        var now = time.GetUtcNow();
        if (link.ExpiresAt is { } expiresAt && expiresAt <= now)
        {
            return new RedirectOutcome(RedirectKind.Expired);
        }

        var counted = false;
        if (link.MaxClicks is { } budget)
        {
            if (recordClick)
            {
                if (!await links.TryConsumeClickAsync(slug, now, cancellationToken))
                {
                    return new RedirectOutcome(RedirectKind.Exhausted);
                }

                counted = true;
            }
            else if (link.Clicks >= budget)
            {
                return new RedirectOutcome(RedirectKind.Exhausted);
            }
        }

        if (recordClick)
        {
            clicks.TryEnqueue(Describe(slug, http, now, counted));
        }

        return new RedirectOutcome(RedirectKind.Found, link.Url);
    }

    private ClickEvent Describe(string slug, HttpContext http, DateTimeOffset now, bool counted)
    {
        var userAgent = http.Request.Headers.UserAgent.ToString();
        var client = UserAgentParser.Parse(userAgent);

        return new ClickEvent(
            slug,
            now,
            visitors.Hash(ClientAddress.Of(http), userAgent, now),
            ReferrerParser.HostOf(http.Request.Headers.Referer.ToString()),
            client.Browser,
            client.Os,
            client.Device,
            counted);
    }
}
