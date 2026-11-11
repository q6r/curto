using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Curto.Api.Infrastructure;
using Curto.Api.Models;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace Curto.Api.Services;

/// <summary>
/// Fans click events out to live-stream subscribers. Each subscriber owns a small bounded buffer; a subscriber that
/// cannot keep up is disconnected instead of being allowed to hold memory or slow the others down.
/// </summary>
internal sealed class EventBroadcaster
{
    /// <summary>Frames buffered per subscriber before it counts as too slow.</summary>
    public const int SubscriberBuffer = 256;

    private readonly ConcurrentDictionary<long, Subscription> _subscribers = new();
    private readonly JsonSerializerOptions _json;
    private readonly int _maxSubscribers;
    private int _count;
    private long _nextId;
    private long _slowConsumersDropped;

    /// <summary>Creates the broadcaster.</summary>
    /// <param name="options">Source of the subscriber limit.</param>
    /// <param name="json">Serialiser settings shared with the HTTP API.</param>
    public EventBroadcaster(IOptions<CurtoOptions> options, IOptions<JsonOptions> json)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(json);

        _maxSubscribers = options.Value.SseMaxSubscribers;
        _json = json.Value.SerializerOptions;
    }

    /// <summary>Subscribers connected right now.</summary>
    public int SubscriberCount => Volatile.Read(ref _count);

    /// <summary>Subscribers disconnected because their buffer filled up.</summary>
    public long SlowConsumersDropped => Interlocked.Read(ref _slowConsumersDropped);

    /// <summary>Registers a subscriber.</summary>
    /// <returns>The subscription, or null when the subscriber limit is reached.</returns>
    public Subscription? TrySubscribe()
    {
        if (Interlocked.Increment(ref _count) > _maxSubscribers)
        {
            Interlocked.Decrement(ref _count);
            return null;
        }

        var subscription = new Subscription(Interlocked.Increment(ref _nextId), this);
        _subscribers[subscription.Id] = subscription;
        return subscription;
    }

    /// <summary>Pushes a click to every subscriber. Cheap when nobody listens.</summary>
    /// <param name="click">Event to publish.</param>
    public void Publish(ClickEvent click)
    {
        if (_subscribers.IsEmpty)
        {
            return;
        }

        // Serialised once, shared by every subscriber.
        var frame = SseFrame.Event("click", JsonSerializer.Serialize(ClickNotification.From(click), _json));

        foreach (var (_, subscription) in _subscribers)
        {
            if (!subscription.TryEnqueue(frame))
            {
                Interlocked.Increment(ref _slowConsumersDropped);
                subscription.Drop();
            }
        }
    }

    private void Remove(Subscription subscription)
    {
        if (_subscribers.TryRemove(subscription.Id, out _))
        {
            Interlocked.Decrement(ref _count);
        }
    }

    /// <summary>One connected client. Dispose it when the connection ends.</summary>
    internal sealed class Subscription : IDisposable
    {
        private readonly EventBroadcaster _owner;
        private readonly Channel<string> _frames = Channel.CreateBounded<string>(
            new BoundedChannelOptions(SubscriberBuffer)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            });

        private int _disposed;
        private int _dropped;

        internal Subscription(long id, EventBroadcaster owner)
        {
            Id = id;
            _owner = owner;
        }

        /// <summary>Identifier within the broadcaster.</summary>
        public long Id { get; }

        /// <summary>True when the broadcaster disconnected this subscriber for being too slow.</summary>
        public bool WasDropped => Volatile.Read(ref _dropped) == 1;

        /// <summary>Complete SSE frames, ready to be written to the response.</summary>
        public ChannelReader<string> Frames => _frames.Reader;

        internal bool TryEnqueue(string frame) => _frames.Writer.TryWrite(frame);

        internal void Drop()
        {
            Volatile.Write(ref _dropped, 1);
            _frames.Writer.TryComplete();
            _owner.Remove(this);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _frames.Writer.TryComplete();
                _owner.Remove(this);
            }
        }
    }
}

/// <summary>Formatting of Server-Sent Events frames.</summary>
internal static class SseFrame
{
    /// <summary>A named event with a single-line JSON payload.</summary>
    /// <param name="name">Event name.</param>
    /// <param name="json">Compact JSON (no line breaks).</param>
    public static string Event(string name, string json) => $"event: {name}\ndata: {json}\n\n";

    /// <summary>A comment frame: ignored by clients, keeps idle connections open through proxies.</summary>
    /// <param name="text">Comment text.</param>
    public static string Comment(string text) => $": {text}\n\n";
}
