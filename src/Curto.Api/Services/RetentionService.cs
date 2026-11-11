using Curto.Api.Infrastructure;
using Curto.Api.Storage;
using Microsoft.Extensions.Options;

namespace Curto.Api.Services;

/// <summary>
/// Deletes click rows older than the retention period: once shortly after start-up (the process may have been down at the
/// usual hour) and then every night at 03:00 UTC. Link counters are unaffected: they are lifetime totals.
/// </summary>
/// <param name="store">Click persistence.</param>
/// <param name="options">Source of the retention period.</param>
/// <param name="time">Clock.</param>
/// <param name="logger">Diagnostics.</param>
internal sealed partial class RetentionService(
    ClickStore store,
    IOptions<CurtoOptions> options,
    TimeProvider time,
    ILogger<RetentionService> logger) : BackgroundService
{
    /// <summary>Time of day (UTC) of the nightly run.</summary>
    public static readonly TimeSpan NightlyRunAt = TimeSpan.FromHours(3);

    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    /// <summary>Deletes the click rows that fell out of the retention window.</summary>
    /// <param name="cancellationToken">Cancels the pruning between chunks.</param>
    /// <returns>Number of deleted rows.</returns>
    public async Task<long> PruneOnceAsync(CancellationToken cancellationToken)
    {
        var days = options.Value.RetentionDays;
        var removed = await store.PruneAsync(time.GetUtcNow().AddDays(-days), cancellationToken);
        LogPruned(logger, removed, days);
        return removed;
    }

    /// <summary>How long to wait from <paramref name="now"/> until the next nightly run.</summary>
    /// <param name="now">Current instant.</param>
    public static TimeSpan UntilNextRun(DateTimeOffset now)
    {
        var utc = now.ToUniversalTime();
        var next = new DateTimeOffset(utc.Date, TimeSpan.Zero) + NightlyRunAt;
        if (next <= utc)
        {
            next = next.AddDays(1);
        }

        return next - utc;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
            while (true)
            {
                await RunGuardedAsync(stoppingToken);
                await Task.Delay(UntilNextRun(time.GetUtcNow()), stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private async Task RunGuardedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await PruneOnceAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A failed night must not take the host down; the next run tries again.
            LogFailed(logger, exception);
        }
    }

    [LoggerMessage(EventId = 30, Level = LogLevel.Information, Message = "Retention: removed {Removed} click rows older than {Days} days")]
    private static partial void LogPruned(ILogger logger, long removed, int days);

    [LoggerMessage(EventId = 31, Level = LogLevel.Error, Message = "Retention cleanup failed")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
