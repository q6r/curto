using System.Threading.Channels;
using Curto.Api.Infrastructure;
using Curto.Api.Models;
using Curto.Api.Storage;
using Microsoft.Extensions.Options;

namespace Curto.Api.Services;

/// <summary>
/// Drains the click channel: each event is pushed to live subscribers immediately and collected into a batch that is
/// written to SQLite in a single transaction, either when it is full or when the flush interval has passed.
/// On shutdown the channel is completed first and the loop runs until the buffer is empty, so queued clicks survive a restart.
/// </summary>
internal sealed partial class ClickWriterService(
    ClickChannel channel,
    ClickStore store,
    EventBroadcaster broadcaster,
    IOptions<CurtoOptions> options,
    ILogger<ClickWriterService> logger) : BackgroundService
{
    private const int MaxAttempts = 3;

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        channel.Complete();
        await base.StopAsync(cancellationToken);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value.Clicks;
        var window = TimeSpan.FromMilliseconds(settings.FlushIntervalMs);
        var batch = new List<ClickEvent>(settings.BatchSize);

        // Deliberately not stoppingToken: after StopAsync completes the channel, the loop must drain it.
        while (await channel.Reader.WaitToReadAsync(CancellationToken.None))
        {
            await FillBatchAsync(batch, settings.BatchSize, window);
            await PersistAsync(batch);
            batch.Clear();
        }
    }

    private async Task FillBatchAsync(List<ClickEvent> batch, int batchSize, TimeSpan window)
    {
        using var flushWindow = new CancellationTokenSource(window);
        try
        {
            while (batch.Count < batchSize)
            {
                if (channel.Reader.TryRead(out var click))
                {
                    batch.Add(click);
                    broadcaster.Publish(click);
                    continue;
                }

                if (!await channel.Reader.WaitToReadAsync(flushWindow.Token))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The flush window elapsed: write what we have.
        }
    }

    private async Task PersistAsync(List<ClickEvent> batch)
    {
        if (batch.Count == 0)
        {
            return;
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await store.InsertBatchAsync(batch, CancellationToken.None);
                channel.RecordPersisted(batch.Count);
                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A background loop must outlive a failing disk: retry with back-off, then give up on this batch only.
                if (attempt >= MaxAttempts)
                {
                    LogBatchLost(logger, exception, batch.Count);
                    channel.RecordFailed(batch.Count);
                    return;
                }

                LogRetry(logger, exception, attempt, batch.Count);
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), CancellationToken.None);
            }
        }
    }

    [LoggerMessage(EventId = 20, Level = LogLevel.Warning, Message = "Writing {Count} click events failed (attempt {Attempt}); retrying")]
    private static partial void LogRetry(ILogger logger, Exception exception, int attempt, int count);

    [LoggerMessage(EventId = 21, Level = LogLevel.Error, Message = "Giving up on a batch of {Count} click events")]
    private static partial void LogBatchLost(ILogger logger, Exception exception, int count);
}
