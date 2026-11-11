namespace Curto.Api.Infrastructure;

/// <summary>
/// Strongly typed settings. They are bound from the <c>Curto</c> section of the configuration
/// (<c>appsettings.json</c>, <c>Curto__Name</c> variables) and from the documented <c>CURTO_*</c> aliases,
/// then validated at start-up by <see cref="CurtoOptionsValidator"/>.
/// </summary>
internal sealed class CurtoOptions
{
    /// <summary>Name of the configuration section.</summary>
    public const string SectionName = "Curto";

    /// <summary>Public origin used to build short URLs, e.g. <c>https://s.example.com</c>. Empty derives it from the request.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>SQLite database file. Relative paths are resolved against the content root.</summary>
    public string DbPath { get; set; } = "data/curto.db";

    /// <summary>Secret expected in the <c>X-API-Key</c> header. Required outside Development; generated (and printed once) in Development.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Reject target URLs that point at localhost, private, link-local or otherwise non-public addresses.</summary>
    public bool BlockPrivateTargets { get; set; }

    /// <summary>Click rows older than this many days are deleted by the nightly cleanup.</summary>
    public int RetentionDays { get; set; } = 180;

    /// <summary>Largest accepted request body, in bytes.</summary>
    public int MaxRequestBodyBytes { get; set; } = 16 * 1024;

    /// <summary>Seconds between heartbeat comments on the live event stream.</summary>
    public int SseHeartbeatSeconds { get; set; } = 15;

    /// <summary>Maximum number of simultaneous live-stream subscribers.</summary>
    public int SseMaxSubscribers { get; set; } = 100;

    /// <summary>Per-client request budgets.</summary>
    public RateLimitSettings RateLimits { get; set; } = new();

    /// <summary>Tuning of the asynchronous click pipeline.</summary>
    public ClickPipelineSettings Clicks { get; set; } = new();
}

/// <summary>Fixed-window request budgets, per client address and per minute.</summary>
internal sealed class RateLimitSettings
{
    /// <summary>Redirects and QR codes.</summary>
    public int RedirectPerMinute { get; set; } = 120;

    /// <summary>Link creation.</summary>
    public int CreatePerMinute { get; set; } = 20;

    /// <summary>Every other management endpoint, including the live stream handshake.</summary>
    public int ApiPerMinute { get; set; } = 300;
}

/// <summary>Tuning of the bounded click queue and its batching writer.</summary>
internal sealed class ClickPipelineSettings
{
    /// <summary>Events buffered in memory; the oldest event is dropped when the buffer is full.</summary>
    public int QueueCapacity { get; set; } = 10_000;

    /// <summary>Most events written in one transaction.</summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>Longest time an event waits for company before its batch is written.</summary>
    public int FlushIntervalMs { get; set; } = 250;
}
