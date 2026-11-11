using System.Threading.Channels;
using Curto.Api.Infrastructure;
using Curto.Api.Services;
using Microsoft.Extensions.Options;

namespace Curto.Api.Endpoints;

/// <summary>The live click stream: <c>GET /api/events</c> (Server-Sent Events).</summary>
internal static class EventsEndpoints
{
    /// <summary>Maps the stream below the (already authorised) API group.</summary>
    /// <param name="api">The <c>/api</c> group.</param>
    public static void MapEventsEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        api.MapGet("/events", StreamAsync)
            .WithTags("Live")
            .WithName("ClickStream")
            .WithSummary("Live click stream (Server-Sent Events)")
            .WithDescription(
                "Pushes one `click` event per redirect as it happens: `{slug, at, referrer, browser, os, device}`. " +
                "A `: heartbeat` comment is sent periodically (15 s by default). Authentication is header-only (X-API-Key), " +
                "so read the stream with fetch() rather than EventSource. Subscribers that cannot keep up are disconnected.")
            .Produces<string>(StatusCodes.Status200OK, "text/event-stream")
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<IResult> StreamAsync(
        HttpContext http,
        EventBroadcaster broadcaster,
        IOptions<CurtoOptions> options,
        IHostApplicationLifetime lifetime)
    {
        using var subscription = broadcaster.TrySubscribe();
        if (subscription is null)
        {
            return Problems.ServiceUnavailable("Too many live subscribers. Retry shortly.", retryAfterSeconds: 5);
        }

        var response = http.Response;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-store";
        response.Headers["X-Accel-Buffering"] = "no"; // ask nginx-style proxies not to buffer the stream

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, lifetime.ApplicationStopping);
        var cancellationToken = linked.Token;
        var heartbeatEvery = TimeSpan.FromSeconds(options.Value.SseHeartbeatSeconds);

        try
        {
            await WriteAsync(response, "retry: 5000\n" + SseFrame.Comment("connected"), cancellationToken);
            await PumpAsync(response, subscription.Frames, heartbeatEvery, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The client disconnected or the application is shutting down: nothing to report.
        }
        finally
        {
            await linked.CancelAsync();
        }

        return TypedResults.Empty;
    }

    private static async Task PumpAsync(HttpResponse response, ChannelReader<string> frames, TimeSpan heartbeatEvery, CancellationToken cancellationToken)
    {
        using var heartbeat = new PeriodicTimer(heartbeatEvery);
        var nextFrame = frames.WaitToReadAsync(cancellationToken).AsTask();
        var nextBeat = heartbeat.WaitForNextTickAsync(cancellationToken).AsTask();

        while (true)
        {
            var finished = await Task.WhenAny(nextFrame, nextBeat);
            if (finished == nextBeat)
            {
                if (!await nextBeat)
                {
                    return;
                }

                await WriteAsync(response, SseFrame.Comment("heartbeat"), cancellationToken);
                nextBeat = heartbeat.WaitForNextTickAsync(cancellationToken).AsTask();
                continue;
            }

            if (!await nextFrame)
            {
                return; // the broadcaster completed this subscription (slow consumer) or the server is stopping
            }

            while (frames.TryRead(out var frame))
            {
                await WriteAsync(response, frame, cancellationToken);
            }

            nextFrame = frames.WaitToReadAsync(cancellationToken).AsTask();
        }
    }

    private static async Task WriteAsync(HttpResponse response, string text, CancellationToken cancellationToken)
    {
        await response.WriteAsync(text, cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }
}
