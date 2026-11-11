using System.Globalization;
using System.Net;
using System.Text.Json;
using Curto.Api.Models;
using Curto.Api.Storage;
using Curto.Api.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Curto.Api.Tests;

/// <summary>
/// Seeds one dataset with exact timestamps around every window boundary. The clock starts at 2026-03-15T10:30:00Z and is
/// moved two hours on after seeding, so the numbers asserted in <see cref="StatsTests"/> can be verified by hand.
/// </summary>
public sealed class StatsFactory : CurtoFactory
{
    private static DateTimeOffset At(string utc) => DateTimeOffset.Parse(utc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    private static ClickEvent Click(string slug, string at, long visitor, string? referrer, string browser, string os, string device) =>
        new(slug, At(at), visitor, referrer, browser, os, device, Counted: true);

    public override async Task InitializeAsync()
    {
        using var client = CreateApiClient();

        // alpha stays active; bravo is created with an expiry and has expired once the clock is moved on below.
        await CreateLinkAsync(client, "https://example.com/alpha", body => { body["slug"] = "alpha-link"; body["title"] = "Alpha"; });
        await CreateLinkAsync(client, "https://example.com/bravo", body =>
        {
            body["slug"] = "bravo-link";
            body["title"] = "Bravo";
            body["expiresAt"] = Clock.GetUtcNow().AddHours(1);
        });
        await CreateLinkAsync(client, "https://example.com/quiet", body => body["slug"] = "quiet-link");

        await Services.GetRequiredService<ClickStore>().InsertBatchAsync(
        [
            Click("alpha-link", "2026-03-15T09:10:00Z", 1, "news.ycombinator.com", "Chrome", "Windows", "desktop"),
            Click("alpha-link", "2026-03-15T09:45:00Z", 1, "news.ycombinator.com", "Chrome", "Windows", "desktop"),
            Click("bravo-link", "2026-03-15T10:05:00Z", 2, null, "Safari", "iOS", "mobile"),
            Click("alpha-link", "2026-03-14T23:59:59Z", 3, "t.co", "Firefox", "Linux", "desktop"),
            Click("bravo-link", "2026-03-09T00:00:00Z", 4, null, "Chrome", "Windows", "desktop"),         // first second of the 7d window
            Click("bravo-link", "2026-03-08T23:59:59Z", 5, "example.org", "Chrome", "Windows", "desktop"), // last second before it
            Click("alpha-link", "2026-03-02T00:00:00Z", 6, null, "Chrome", "Windows", "desktop"),         // first second of the previous 7d window
            Click("alpha-link", "2026-03-01T23:59:59Z", 7, null, "Chrome", "Windows", "desktop"),         // before the previous window
            Click("alpha-link", "2026-03-16T00:00:00Z", 8, null, "Chrome", "Windows", "desktop"),         // exactly at the end of the window
        ], CancellationToken.None);

        // Two hours later bravo has expired. The day buckets of the 7d window do not move; the hour buckets of 24h do.
        Clock.Advance(TimeSpan.FromHours(2));
    }
}

public sealed class StatsTests : IClassFixture<StatsFactory>, IDisposable
{
    private readonly StatsFactory _factory;
    private readonly HttpClient _client;

    public StatsTests(StatsFactory factory)
    {
        _factory = factory;
        _client = factory.CreateApiClient();
    }

    public void Dispose() => _client.Dispose();

    private async Task<JsonElement> GetAsync(string path)
    {
        using var response = await _client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.JsonAsync();
    }

    private static long[] Column(JsonElement stats, string name) =>
        [.. stats.GetProperty("series").EnumerateArray().Select(point => point.Num(name))];

    private static (string Name, long Clicks)[] Ranked(JsonElement stats, string list) =>
        [.. stats.GetProperty(list).EnumerateArray().Select(item => (item.Str("name"), item.Num("clicks")))];

    private static (long Total, long Active, long Expired) LinkCounts(JsonElement stats)
    {
        var links = stats.GetProperty("links");
        return (links.Num("total"), links.Num("active"), links.Num("expired"));
    }

    private static (long InRange, long Previous) Pair(JsonElement stats, string name) =>
        (stats.GetProperty(name).Num("inRange"), stats.GetProperty(name).Num("previous"));

    [Fact]
    public async Task Overview_7d_adds_up_to_the_hand_computed_numbers()
    {
        var stats = await GetAsync("/api/stats/overview?range=7d");

        Assert.Equal(
            ["range", "links", "clicks", "visitors", "series", "topLinks", "referrers", "devices", "browsers", "os"],
            stats.Names());

        var range = stats.GetProperty("range");
        Assert.Equal(["from", "to", "bucket"], range.Names());
        Assert.Equal("2026-03-09T00:00:00Z", range.Str("from"));
        Assert.Equal("2026-03-16T00:00:00Z", range.Str("to"));
        Assert.Equal("day", range.Str("bucket"));

        Assert.Equal((3L, 2L, 1L), LinkCounts(stats));
        Assert.Equal((5L, 2L), Pair(stats, "clicks"));
        Assert.Equal((4L, 2L), Pair(stats, "visitors"));

        Assert.Equal(7, stats.GetProperty("series").GetArrayLength());
        Assert.Equal([1L, 0, 0, 0, 0, 1, 3], Column(stats, "clicks"));
        Assert.Equal([1L, 0, 0, 0, 0, 1, 2], Column(stats, "visitors"));
        Assert.Equal(
            ["2026-03-09T00:00:00Z", "2026-03-10T00:00:00Z", "2026-03-11T00:00:00Z", "2026-03-12T00:00:00Z", "2026-03-13T00:00:00Z", "2026-03-14T00:00:00Z", "2026-03-15T00:00:00Z"],
            [.. stats.GetProperty("series").EnumerateArray().Select(point => point.Str("t"))]);

        var top = stats.GetProperty("topLinks").EnumerateArray().ToArray();
        Assert.Equal(2, top.Length);
        Assert.Equal(["slug", "title", "url", "clicks"], top[0].Names());
        Assert.Equal(("alpha-link", "Alpha", "https://example.com/alpha", 3L), (top[0].Str("slug"), top[0].Str("title"), top[0].Str("url"), top[0].Num("clicks")));
        Assert.Equal(("bravo-link", 2L), (top[1].Str("slug"), top[1].Num("clicks")));

        Assert.Equal([("(direct)", 2L), ("news.ycombinator.com", 2L), ("t.co", 1L)], Ranked(stats, "referrers"));
        Assert.Equal([("desktop", 4L), ("mobile", 1L)], Ranked(stats, "devices"));
        Assert.Equal([("Chrome", 3L), ("Firefox", 1L), ("Safari", 1L)], Ranked(stats, "browsers"));
        Assert.Equal([("Windows", 3L), ("Linux", 1L), ("iOS", 1L)], Ranked(stats, "os"));
    }

    [Fact]
    public async Task Omitting_the_range_means_7d()
    {
        var explicitRange = await GetAsync("/api/stats/overview?range=7d");
        var defaulted = await GetAsync("/api/stats/overview");

        Assert.Equal(explicitRange.ToString(), defaulted.ToString());
    }

    [Fact]
    public async Task Overview_24h_uses_hourly_buckets_aligned_to_the_hour_of_now()
    {
        // The clock reads 12:30, so the window is [2026-03-14T13:00Z, 2026-03-15T13:00Z) in 24 hourly buckets.
        var stats = await GetAsync("/api/stats/overview?range=24h");

        var range = stats.GetProperty("range");
        Assert.Equal("2026-03-14T13:00:00Z", range.Str("from"));
        Assert.Equal("2026-03-15T13:00:00Z", range.Str("to"));
        Assert.Equal("hour", range.Str("bucket"));

        Assert.Equal((4L, 0L), Pair(stats, "clicks"));
        Assert.Equal((3L, 0L), Pair(stats, "visitors"));

        var clicks = Column(stats, "clicks");
        Assert.Equal(24, clicks.Length);
        Assert.Equal(1L, clicks[10]);  // 2026-03-14T23:00Z holds the click at 23:59:59
        Assert.Equal(2L, clicks[20]);  // 09:00Z holds two clicks of the same visitor
        Assert.Equal(1L, clicks[21]);  // 10:00Z
        Assert.Equal(4L, clicks.Sum());
        Assert.Equal(1L, Column(stats, "visitors")[20]);

        var points = stats.GetProperty("series").EnumerateArray().ToArray();
        Assert.Equal("2026-03-14T13:00:00Z", points[0].Str("t"));
        Assert.Equal("2026-03-15T09:00:00Z", points[20].Str("t"));
        Assert.Equal("2026-03-15T12:00:00Z", points[23].Str("t"));

        Assert.Equal((3L, 2L, 1L), LinkCounts(stats));
        Assert.Equal(
            [("alpha-link", 3L), ("bravo-link", 1L)],
            [.. stats.GetProperty("topLinks").EnumerateArray().Select(l => (l.Str("slug"), l.Num("clicks")))]);
    }

    [Theory]
    [InlineData("30d", 30, "2026-02-14T00:00:00Z", 8L, 0L)]
    [InlineData("90d", 90, "2025-12-16T00:00:00Z", 8L, 0L)]
    public async Task Longer_ranges_use_one_daily_bucket_per_day(string range, int buckets, string from, long clicksInRange, long clicksPrevious)
    {
        var stats = await GetAsync($"/api/stats/overview?range={range}");

        Assert.Equal(from, stats.GetProperty("range").Str("from"));
        Assert.Equal("2026-03-16T00:00:00Z", stats.GetProperty("range").Str("to"));
        Assert.Equal("day", stats.GetProperty("range").Str("bucket"));
        Assert.Equal(buckets, stats.GetProperty("series").GetArrayLength());
        Assert.Equal((clicksInRange, clicksPrevious), Pair(stats, "clicks"));
    }

    [Theory]
    [InlineData("24h")]
    [InlineData("7d")]
    [InlineData("30d")]
    [InlineData("90d")]
    public async Task The_series_and_every_breakdown_add_up_to_the_total(string range)
    {
        var stats = await GetAsync($"/api/stats/overview?range={range}");
        var total = stats.GetProperty("clicks").Num("inRange");

        Assert.Equal(total, Column(stats, "clicks").Sum());
        Assert.Equal(total, Ranked(stats, "devices").Sum(d => d.Clicks));
        Assert.Equal(total, Ranked(stats, "browsers").Sum(d => d.Clicks));
        Assert.Equal(total, Ranked(stats, "os").Sum(d => d.Clicks));
        Assert.Equal(total, Ranked(stats, "referrers").Sum(d => d.Clicks));
        Assert.Equal(total, stats.GetProperty("topLinks").EnumerateArray().Sum(l => l.Num("clicks")));
    }

    [Fact]
    public async Task Link_stats_have_the_same_shape_restricted_to_one_link()
    {
        var stats = await GetAsync("/api/links/alpha-link/stats?range=7d");

        Assert.Equal(
            ["range", "links", "clicks", "visitors", "series", "topLinks", "referrers", "devices", "browsers", "os"],
            stats.Names());
        Assert.Equal((1L, 1L, 0L), LinkCounts(stats));
        Assert.Equal((3L, 1L), Pair(stats, "clicks"));
        Assert.Equal((2L, 1L), Pair(stats, "visitors"));
        Assert.Equal([0L, 0, 0, 0, 0, 1, 2], Column(stats, "clicks"));
        Assert.Equal([("alpha-link", 3L)], [.. stats.GetProperty("topLinks").EnumerateArray().Select(l => (l.Str("slug"), l.Num("clicks")))]);
        Assert.Equal([("news.ycombinator.com", 2L), ("t.co", 1L)], Ranked(stats, "referrers"));
        Assert.Equal([("desktop", 3L)], Ranked(stats, "devices"));
        Assert.Equal([("Chrome", 2L), ("Firefox", 1L)], Ranked(stats, "browsers"));
        Assert.Equal([("Windows", 2L), ("Linux", 1L)], Ranked(stats, "os"));
    }

    [Fact]
    public async Task An_expired_link_reports_itself_as_expired_in_its_own_stats()
    {
        var stats = await GetAsync("/api/links/bravo-link/stats?range=7d");

        Assert.Equal((1L, 0L, 1L), LinkCounts(stats));
        Assert.Equal((2L, 1L), Pair(stats, "clicks"));
    }

    [Fact]
    public async Task A_link_without_clicks_still_gets_a_full_zero_filled_series()
    {
        var stats = await GetAsync("/api/links/quiet-link/stats?range=24h");

        Assert.Equal(24, stats.GetProperty("series").GetArrayLength());
        Assert.All(Column(stats, "clicks"), clicks => Assert.Equal(0L, clicks));
        Assert.Equal((0L, 0L), Pair(stats, "clicks"));
        Assert.Empty(stats.GetProperty("topLinks").EnumerateArray());
        Assert.Empty(stats.GetProperty("referrers").EnumerateArray());
        Assert.Empty(stats.GetProperty("devices").EnumerateArray());
    }

    [Fact]
    public async Task Deleting_a_link_removes_it_and_its_clicks_from_every_statistic()
    {
        await CurtoFactory.CreateLinkAsync(_client, "https://example.com/doomed", body => body["slug"] = "doomed-link");
        await _factory.Services.GetRequiredService<ClickStore>().InsertBatchAsync(
        [
            new ClickEvent("doomed-link", DateTimeOffset.Parse("2026-03-15T09:00:00Z", CultureInfo.InvariantCulture), 99, "doom.example", "Chrome", "Windows", "desktop", Counted: true),
            new ClickEvent("doomed-link", DateTimeOffset.Parse("2026-03-15T09:01:00Z", CultureInfo.InvariantCulture), 99, "doom.example", "Chrome", "Windows", "desktop", Counted: true),
        ], CancellationToken.None);

        var before = await GetAsync("/api/stats/overview?range=7d");
        using var deleted = await _client.DeleteAsync("/api/links/doomed-link");
        var after = await GetAsync("/api/stats/overview?range=7d");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(before.GetProperty("links").Num("total") - 1, after.GetProperty("links").Num("total"));
        Assert.Equal(before.GetProperty("clicks").Num("inRange") - 2, after.GetProperty("clicks").Num("inRange"));
        Assert.Equal((5L, 2L), Pair(after, "clicks")); // back to the seeded dataset
        Assert.DoesNotContain("doomed-link", after.GetProperty("topLinks").ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("doom.example", after.GetProperty("referrers").ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("range=1y")]
    [InlineData("range=7")]
    [InlineData("range=")]
    [InlineData("range=7D")]
    [InlineData("range=%27%3BDROP%20TABLE%20clicks")]
    public async Task Unknown_ranges_are_rejected(string query)
    {
        using var overview = await _client.GetAsync($"/api/stats/overview?{query}");
        using var link = await _client.GetAsync($"/api/links/alpha-link/stats?{query}");

        var problem = await overview.ExpectProblemAsync(400);
        Assert.True(problem.GetProperty("errors").TryGetProperty("range", out _));
        await link.ExpectProblemAsync(400);
    }

    [Fact]
    public async Task Stats_of_an_unknown_link_are_a_404()
    {
        using var response = await _client.GetAsync("/api/links/no-such-link/stats");

        await response.ExpectProblemAsync(404);
    }
}
