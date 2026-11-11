using System.Text.Json;
using Curto.Api.Infrastructure;
using Curto.Api.Models;
using Curto.Api.Services;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace Curto.Api.Tests;

public sealed class ClickPipelineTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 15, 10, 30, 0, TimeSpan.Zero);

    private static ClickEvent Click(string slug) => new(slug, Now, 1, null, "Chrome", "Windows", "desktop", Counted: false);

    private static ClickChannel Channel(int capacity) =>
        new(Options.Create(new CurtoOptions { Clicks = { QueueCapacity = capacity } }));

    private static EventBroadcaster Broadcaster(int maxSubscribers = 100) =>
        new(Options.Create(new CurtoOptions { SseMaxSubscribers = maxSubscribers }), Options.Create(new JsonOptions()));

    [Fact]
    public void Channel_drops_the_oldest_event_when_full_and_counts_it()
    {
        var channel = Channel(capacity: 3);

        foreach (var slug in new[] { "one", "two", "three", "four", "five" })
        {
            Assert.True(channel.TryEnqueue(Click(slug)));
        }

        var stats = channel.Snapshot();
        Assert.Equal(new ClickQueueStats(Depth: 3, Capacity: 3, Enqueued: 5, Persisted: 0, Dropped: 2, Failed: 0), stats);

        List<string> remaining = [];
        while (channel.Reader.TryRead(out var click))
        {
            remaining.Add(click.Slug);
        }

        Assert.Equal(["three", "four", "five"], remaining);
    }

    [Fact]
    public void Enqueueing_never_blocks_even_when_nobody_reads()
    {
        var channel = Channel(capacity: 16);

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 100_000; i++)
        {
            channel.TryEnqueue(Click("slug"));
        }

        elapsed.Stop();

        Assert.Equal(100_000 - 16, channel.Snapshot().Dropped);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), $"took {elapsed.Elapsed}");
    }

    [Fact]
    public void Completed_channel_refuses_new_events_but_keeps_the_backlog()
    {
        var channel = Channel(capacity: 4);
        channel.TryEnqueue(Click("kept"));

        channel.Complete();

        Assert.False(channel.TryEnqueue(Click("late")));
        Assert.True(channel.Reader.TryRead(out var click));
        Assert.Equal("kept", click.Slug);
    }

    [Fact]
    public void Persisted_and_failed_counters_accumulate()
    {
        var channel = Channel(capacity: 4);

        channel.RecordPersisted(5);
        channel.RecordPersisted(2);
        channel.RecordFailed(3);

        var stats = channel.Snapshot();
        Assert.Equal(7, stats.Persisted);
        Assert.Equal(3, stats.Failed);
    }

    [Fact]
    public async Task Broadcaster_delivers_each_click_once_to_every_subscriber()
    {
        var broadcaster = Broadcaster();
        using var first = broadcaster.TrySubscribe()!;
        using var second = broadcaster.TrySubscribe()!;

        broadcaster.Publish(Click("abc"));

        foreach (var subscription in new[] { first, second })
        {
            Assert.True(await subscription.Frames.WaitToReadAsync());
            Assert.True(subscription.Frames.TryRead(out var frame));
            Assert.StartsWith("event: click\ndata: ", frame, StringComparison.Ordinal);
            Assert.EndsWith("\n\n", frame, StringComparison.Ordinal);
            Assert.False(subscription.Frames.TryRead(out _));
        }
    }

    [Fact]
    public async Task Click_payload_has_exactly_the_public_members_and_no_visitor_id()
    {
        var broadcaster = Broadcaster();
        using var subscription = broadcaster.TrySubscribe()!;

        broadcaster.Publish(new ClickEvent("abc", Now, Visitor: 123456789, Referrer: "t.co", "Safari", "iOS", "mobile", Counted: true));

        Assert.True(await subscription.Frames.WaitToReadAsync());
        subscription.Frames.TryRead(out var frame);
        var json = frame!.Split('\n')[1]["data: ".Length..];
        using var document = JsonDocument.Parse(json);

        Assert.Equal(
            ["slug", "at", "referrer", "browser", "os", "device"],
            document.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.DoesNotContain("123456789", json, StringComparison.Ordinal);
        Assert.Equal("t.co", document.RootElement.GetProperty("referrer").GetString());
    }

    [Fact]
    public async Task Direct_traffic_is_reported_as_direct_in_the_stream()
    {
        var broadcaster = Broadcaster();
        using var subscription = broadcaster.TrySubscribe()!;

        broadcaster.Publish(Click("abc"));

        await subscription.Frames.WaitToReadAsync();
        subscription.Frames.TryRead(out var frame);
        Assert.Contains("\"referrer\":\"(direct)\"", frame, StringComparison.Ordinal);
    }

    [Fact]
    public void A_subscriber_that_cannot_keep_up_is_dropped_without_affecting_the_others()
    {
        var broadcaster = Broadcaster();
        using var slow = broadcaster.TrySubscribe()!;
        using var healthy = broadcaster.TrySubscribe()!;

        for (var i = 0; i < EventBroadcaster.SubscriberBuffer + 1; i++)
        {
            broadcaster.Publish(Click("abc"));

            // The healthy client keeps reading, the slow one never does.
            Assert.True(healthy.Frames.TryRead(out _));
        }

        Assert.True(slow.WasDropped);
        Assert.False(healthy.WasDropped);
        Assert.Equal(1, broadcaster.SubscriberCount);
        Assert.Equal(1, broadcaster.SlowConsumersDropped);

        // The dropped subscriber's channel is completed: once its buffer is drained the stream ends.
        var buffered = 0;
        while (slow.Frames.TryRead(out _))
        {
            buffered++;
        }

        Assert.Equal(EventBroadcaster.SubscriberBuffer, buffered);
        Assert.True(slow.Frames.Completion.IsCompleted);
    }

    [Fact]
    public void Subscriber_limit_is_enforced_and_released_on_dispose()
    {
        var broadcaster = Broadcaster(maxSubscribers: 2);
        var a = broadcaster.TrySubscribe();
        var b = broadcaster.TrySubscribe();

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Null(broadcaster.TrySubscribe());
        Assert.Equal(2, broadcaster.SubscriberCount);

        a.Dispose();
        a.Dispose(); // idempotent
        Assert.Equal(1, broadcaster.SubscriberCount);

        using var c = broadcaster.TrySubscribe();
        Assert.NotNull(c);
        b.Dispose();
    }

    [Fact]
    public void Publishing_without_subscribers_does_nothing()
    {
        var broadcaster = Broadcaster();

        broadcaster.Publish(Click("abc"));

        Assert.Equal(0, broadcaster.SubscriberCount);
        Assert.Equal(0, broadcaster.SlowConsumersDropped);
    }

    [Fact]
    public void Sse_frames_follow_the_wire_format()
    {
        Assert.Equal("event: click\ndata: {\"a\":1}\n\n", SseFrame.Event("click", "{\"a\":1}"));
        Assert.Equal(": heartbeat\n\n", SseFrame.Comment("heartbeat"));
    }
}
