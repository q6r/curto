using System.Net;
using System.Text.Json;
using Curto.Api.Services;
using Curto.Api.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Curto.Api.Tests;

public sealed class FewSubscribersFactory : CurtoFactory
{
    protected override void Configure(IDictionary<string, string?> settings) => settings["Curto:SseMaxSubscribers"] = "2";
}

public sealed class SseTests : IClassFixture<CurtoFactory>, IDisposable
{
    private readonly CurtoFactory _factory;
    private readonly HttpClient _client;

    public SseTests(CurtoFactory factory)
    {
        _factory = factory;
        _client = factory.CreateApiClient();
    }

    public void Dispose() => _client.Dispose();

    private EventBroadcaster Broadcaster => _factory.Services.GetRequiredService<EventBroadcaster>();

    // Earlier tests hang up asynchronously: wait until the server has noticed before counting subscribers.
    private Task NoSubscribersAsync() => Eventually.Async(() => Broadcaster.SubscriberCount == 0);

    private async Task VisitAsync(string slug, string? userAgent = null, string? referer = null, string? ip = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/" + slug);
        if (userAgent is not null)
        {
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        }

        if (referer is not null)
        {
            request.Headers.TryAddWithoutValidation("Referer", referer);
        }

        if (ip is not null)
        {
            request.Headers.Add("X-Test-Ip", ip);
        }

        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
    }

    [Fact]
    public async Task The_stream_requires_the_key_in_the_header()
    {
        await NoSubscribersAsync();
        using var anonymous = _factory.CreateApiClient(withKey: false);

        using var none = await anonymous.GetAsync("/api/events");
        using var queryString = await anonymous.GetAsync($"/api/events?apiKey={CurtoFactory.ApiKey}");

        await none.ExpectProblemAsync(401);
        Assert.Equal(HttpStatusCode.Unauthorized, queryString.StatusCode);
        Assert.Equal(0, Broadcaster.SubscriberCount);
    }

    [Fact]
    public async Task A_click_is_pushed_to_the_stream_as_it_happens()
    {
        await CurtoFactory.CreateLinkAsync(_client, "https://example.com/live", body => body["slug"] = "live-one");
        using var sse = await SseClient.OpenAsync(_client);

        Assert.Equal("text/event-stream", sse.Response.Content.Headers.ContentType?.MediaType);
        Assert.True(sse.Response.Headers.CacheControl!.NoStore);
        Assert.Equal("no", sse.Response.Headers.GetValues("X-Accel-Buffering").Single());
        var hello = await sse.ReadFrameAsync();
        Assert.Contains("connected", hello.Comments);

        await VisitAsync(
            "live-one",
            userAgent: "Mozilla/5.0 (iPhone; CPU iPhone OS 17_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.1 Mobile/15E148 Safari/604.1",
            referer: "https://www.t.co/abc?x=1",
            ip: "198.51.100.23");

        var frame = await sse.ReadEventAsync("click");

        using var payload = JsonDocument.Parse(frame.Data!);
        var click = payload.RootElement;
        Assert.Equal(["slug", "at", "referrer", "browser", "os", "device"], click.Names());
        Assert.Equal("live-one", click.Str("slug"));
        Assert.Equal(_factory.Clock.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture), click.Str("at"));
        Assert.Equal("t.co", click.Str("referrer"));
        Assert.Equal("Safari", click.Str("browser"));
        Assert.Equal("iOS", click.Str("os"));
        Assert.Equal("mobile", click.Str("device"));
        Assert.DoesNotContain("198.51.100.23", frame.Data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Direct_visits_report_direct_and_clicks_arrive_in_order()
    {
        await CurtoFactory.CreateLinkAsync(_client, "https://example.com/order", body => body["slug"] = "order-link");
        using var sse = await SseClient.OpenAsync(_client);
        await sse.ReadFrameAsync();

        await VisitAsync("order-link", userAgent: "curl/8.4.0");
        await VisitAsync("order-link", userAgent: "Mozilla/5.0 (X11; Linux x86_64; rv:132.0) Gecko/20100101 Firefox/132.0");

        var first = JsonDocument.Parse((await sse.ReadEventAsync("click")).Data!).RootElement;
        var second = JsonDocument.Parse((await sse.ReadEventAsync("click")).Data!).RootElement;
        Assert.Equal("(direct)", first.Str("referrer"));
        Assert.Equal("bot", first.Str("device"));
        Assert.Equal("Firefox", second.Str("browser"));
    }

    [Fact]
    public async Task Idle_connections_receive_heartbeat_comments()
    {
        using var sse = await SseClient.OpenAsync(_client);
        await sse.ReadFrameAsync(); // retry + connected

        var beat = await sse.ReadFrameAsync(TimeSpan.FromSeconds(10));

        Assert.Null(beat.Event);
        Assert.Contains("heartbeat", beat.Comments);
    }

    [Fact]
    public async Task Disconnecting_releases_the_subscriber()
    {
        await NoSubscribersAsync();

        var sse = await SseClient.OpenAsync(_client);
        await sse.ReadFrameAsync();
        Assert.Equal(1, Broadcaster.SubscriberCount);

        sse.Dispose();

        await Eventually.Async(() => Broadcaster.SubscriberCount == 0);
    }

    [Fact]
    public async Task Many_subscribers_all_receive_every_click()
    {
        await NoSubscribersAsync();
        await CurtoFactory.CreateLinkAsync(_client, "https://example.com/fanout", body => body["slug"] = "fanout-link");
        List<SseClient> streams = [];
        try
        {
            for (var i = 0; i < 12; i++)
            {
                var stream = await SseClient.OpenAsync(_client);
                streams.Add(stream);
                await stream.ReadFrameAsync();
            }

            Assert.Equal(12, Broadcaster.SubscriberCount);

            await VisitAsync("fanout-link", userAgent: "FanoutAgent/1");
            await VisitAsync("fanout-link", userAgent: "FanoutAgent/2");

            foreach (var stream in streams)
            {
                Assert.Equal("fanout-link", JsonDocument.Parse((await stream.ReadEventAsync("click")).Data!).RootElement.Str("slug"));
                Assert.Equal("fanout-link", JsonDocument.Parse((await stream.ReadEventAsync("click")).Data!).RootElement.Str("slug"));
            }
        }
        finally
        {
            foreach (var stream in streams)
            {
                stream.Dispose();
            }
        }

        await Eventually.Async(() => Broadcaster.SubscriberCount == 0);
    }

    [Fact]
    public async Task Clicks_that_are_not_redirects_do_not_reach_the_stream_and_streams_do_not_slow_the_redirect()
    {
        await CurtoFactory.CreateLinkAsync(_client, "https://example.com/quiet", body => body["slug"] = "quiet-link");
        using var sse = await SseClient.OpenAsync(_client);
        await sse.ReadFrameAsync();

        using var head = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/quiet-link"));
        using var unknown = await _client.GetAsync("/unknown-link");
        await VisitAsync("quiet-link", userAgent: "RealVisitor/1");

        var frame = await sse.ReadEventAsync("click");
        Assert.Equal("quiet-link", JsonDocument.Parse(frame.Data!).RootElement.Str("slug"));
        Assert.Equal(HttpStatusCode.Found, head.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }
}

public sealed class SseLimitTests : IClassFixture<FewSubscribersFactory>, IDisposable
{
    private readonly HttpClient _client;

    public SseLimitTests(FewSubscribersFactory factory) => _client = factory.CreateApiClient();

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task The_subscriber_limit_answers_503_and_frees_up_on_disconnect()
    {
        var first = await SseClient.OpenAsync(_client);
        await first.ReadFrameAsync();
        using var second = await SseClient.OpenAsync(_client);
        await second.ReadFrameAsync();

        using var third = await _client.GetAsync("/api/events");

        await third.ExpectProblemAsync(503);
        Assert.Equal("5", third.Headers.GetValues("Retry-After").Single());

        first.Dispose();

        // The slot is released asynchronously, once the server notices the disconnect.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        HttpStatusCode status;
        do
        {
            using var retry = await SseClient.OpenAsync(_client);
            status = retry.Response.StatusCode;
            if (status != HttpStatusCode.OK)
            {
                await Task.Delay(20);
            }
        }
        while (status != HttpStatusCode.OK && DateTime.UtcNow < deadline);

        Assert.Equal(HttpStatusCode.OK, status);
    }
}
