namespace Curto.Api.Models;

/// <summary>Lifecycle state of a short link at a given instant.</summary>
internal enum LinkStatus
{
    /// <summary>The link redirects.</summary>
    Active,

    /// <summary>The link passed its <c>expiresAt</c> instant.</summary>
    Expired,

    /// <summary>The link reached its <c>maxClicks</c> limit.</summary>
    Exhausted,
}

/// <summary>A stored short link.</summary>
/// <param name="Slug">Public identifier; case-sensitive.</param>
/// <param name="Url">Normalised absolute http(s) target.</param>
/// <param name="Title">Optional human label.</param>
/// <param name="Tags">Lower-case labels used for filtering.</param>
/// <param name="CreatedAt">Creation instant (UTC).</param>
/// <param name="ExpiresAt">Optional instant after which the link answers 410.</param>
/// <param name="MaxClicks">Optional click budget; the link answers 410 once spent.</param>
/// <param name="Clicks">Lifetime redirect counter.</param>
internal sealed record Link(
    string Slug,
    string Url,
    string? Title,
    IReadOnlyList<string> Tags,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    int? MaxClicks,
    long Clicks)
{
    /// <summary>Computes the status at <paramref name="now"/>. Expiry wins over exhaustion.</summary>
    public LinkStatus StatusAt(DateTimeOffset now)
    {
        if (ExpiresAt is { } expiresAt && expiresAt <= now)
        {
            return LinkStatus.Expired;
        }

        if (MaxClicks is { } maxClicks && Clicks >= maxClicks)
        {
            return LinkStatus.Exhausted;
        }

        return LinkStatus.Active;
    }
}
