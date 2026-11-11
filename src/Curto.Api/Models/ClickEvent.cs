namespace Curto.Api.Models;

/// <summary>
/// One recorded redirect. Contains no raw IP address and no raw User-Agent: only the daily-salted visitor
/// hash, the referrer host and the coarse client classification.
/// </summary>
/// <param name="Slug">Slug that was followed.</param>
/// <param name="At">Instant of the redirect (UTC).</param>
/// <param name="Visitor">First 64 bits of HMAC-SHA256(dailySalt, ip + user agent).</param>
/// <param name="Referrer">Referrer host without <c>www.</c>, or null for direct traffic.</param>
/// <param name="Browser">Browser family.</param>
/// <param name="Os">Operating-system family.</param>
/// <param name="Device">Device class: desktop, mobile, tablet, bot or unknown.</param>
/// <param name="Counted">True when the link's click counter was already incremented synchronously (links with a click budget).</param>
internal sealed record ClickEvent(
    string Slug,
    DateTimeOffset At,
    long Visitor,
    string? Referrer,
    string Browser,
    string Os,
    string Device,
    bool Counted)
{
    /// <summary>Label used for traffic without a usable referrer. Cannot collide with a host name.</summary>
    public const string DirectReferrer = "(direct)";
}

/// <summary>The click payload pushed to Server-Sent Events subscribers. The visitor hash is deliberately left out.</summary>
/// <param name="Slug">Slug that was followed.</param>
/// <param name="At">Instant of the redirect (UTC).</param>
/// <param name="Referrer">Referrer host, or <c>(direct)</c>.</param>
/// <param name="Browser">Browser family.</param>
/// <param name="Os">Operating-system family.</param>
/// <param name="Device">Device class.</param>
internal sealed record ClickNotification(
    string Slug,
    DateTimeOffset At,
    string Referrer,
    string Browser,
    string Os,
    string Device)
{
    /// <summary>Projects a stored event onto the public stream shape.</summary>
    public static ClickNotification From(ClickEvent click) =>
        new(click.Slug, click.At, click.Referrer ?? ClickEvent.DirectReferrer, click.Browser, click.Os, click.Device);
}
