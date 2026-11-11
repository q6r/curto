using System.Threading.Channels;
using Curto.Api.Infrastructure;
using Curto.Api.Models;
using Microsoft.Extensions.Options;

namespace Curto.Api.Services;

/// <summary>Counters of the click pipeline, exposed by <c>/readyz</c>.</summary>
/// <param name="Depth">Events waiting in memory.</param>
/// <param name="Capacity">Size of the in-memory buffer.</param>
/// <param name="Enqueued">Events accepted since start-up.</param>
/// <param name="Persisted">Events written to the database.</param>
/// <param name="Dropped">Events discarded because the buffer was full (oldest first).</param>
/// <param name="Failed">Events lost because the database kept rejecting their batch.</param>
internal sealed record ClickQueueStats(int Depth, int Capacity, long Enqueued, long Persisted, long Dropped, long Failed);

/// <summary>
/// The hand-off between the redirect path and the database writer: a bounded channel that never blocks the producer.
/// When the writer falls behind and the buffer fills up, the oldest event is dropped and counted, so a slow disk can
/// degrade analytics but never the redirect itself.
/// </summary>
internal sealed class ClickChannel
{
    private readonly Channel<ClickEvent> _channel;
    private long _enqueued;
    private long _dropped;
    private long _persisted;
    private long _failed;

    /// <summary>Creates the channel sized from the configuration.</summary>
    /// <param name="options">Source of the capacity.</param>
    public ClickChannel(IOptions<CurtoOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        Capacity = options.Value.Clicks.QueueCapacity;
        _channel = Channel.CreateBounded<ClickEvent>(
            new BoundedChannelOptions(Capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            },
            _ => Interlocked.Increment(ref _dropped));
    }

    /// <summary>Size of the in-memory buffer.</summary>
    public int Capacity { get; }

    /// <summary>The consuming side (one reader: the writer service).</summary>
    public ChannelReader<ClickEvent> Reader => _channel.Reader;

    /// <summary>Queues an event. Never blocks; the oldest queued event is dropped when the buffer is full.</summary>
    /// <param name="click">Event to queue.</param>
    /// <returns>False only after <see cref="Complete"/>.</returns>
    public bool TryEnqueue(ClickEvent click)
    {
        if (!_channel.Writer.TryWrite(click))
        {
            return false;
        }

        Interlocked.Increment(ref _enqueued);
        return true;
    }

    /// <summary>Stops accepting events; the reader drains what is left.</summary>
    public void Complete() => _channel.Writer.TryComplete();

    /// <summary>Records events that reached the database.</summary>
    /// <param name="count">Number of events.</param>
    public void RecordPersisted(int count) => Interlocked.Add(ref _persisted, count);

    /// <summary>Records events that could not be written.</summary>
    /// <param name="count">Number of events.</param>
    public void RecordFailed(int count) => Interlocked.Add(ref _failed, count);

    /// <summary>A point-in-time view of the counters.</summary>
    public ClickQueueStats Snapshot() => new(
        _channel.Reader.Count,
        Capacity,
        Interlocked.Read(ref _enqueued),
        Interlocked.Read(ref _persisted),
        Interlocked.Read(ref _dropped),
        Interlocked.Read(ref _failed));
}
