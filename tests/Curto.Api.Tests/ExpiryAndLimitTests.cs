using System.Net;
using System.Net.Http.Json;
using Curto.Api.Tests.Support;

namespace Curto.Api.Tests;

public sealed class ExpiryAndLimitTests : IClassFixture<CurtoFactory>, IDisposable
{
    private readonly CurtoFactory _factory;
    private readonly HttpClient _client;

    public ExpiryAndLimitTests(CurtoFactory factory)
    {
        _factory = factory;
        _client = factory.CreateApiClient();
    }

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task A_link_answers_410_once_it_has_expired()
    {
        var expiresAt = _factory.Clock.GetUtcNow().AddHours(1);
        var link = await CurtoFactory.CreateLinkAsync(_client, "https://example.com/expiring", body =>
        {
            body["slug"] = "expiring-1";
            body["expiresAt"] = expiresAt;
        });
        Assert.Equal("active", link.Str("status"));

        using var before = await _client.GetAsync("/expiring-1");
        _factory.Clock.Advance(TimeSpan.FromHours(1) - TimeSpan.FromSeconds(1));
        using var lastSecond = await _client.GetAsync("/expiring-1");
        _factory.Clock.Advance(TimeSpan.FromSeconds(1));
        using var after = await _client.GetAsync("/expiring-1");

        Assert.Equal(HttpStatusCode.Found, before.StatusCode);
        Assert.Equal(HttpStatusCode.Found, lastSecond.StatusCode);
        var problem = await after.ExpectProblemAsync(410);
        Assert.Equal("link_expired", problem.Str("code"));
        Assert.True(after.Headers.CacheControl!.NoStore);

        using var get = await _client.GetAsync("/api/links/expiring-1");
        Assert.Equal("expired", (await get.JsonAsync()).Str("status"));
    }

    [Fact]
    public async Task Expired_links_do_not_record_clicks()
    {
        await CurtoFactory.CreateLinkAsync(_client, "https://example.com/quiet", body =>
        {
            body["slug"] = "expired-quiet";
            body["expiresAt"] = _factory.Clock.GetUtcNow().AddMinutes(5);
        });
        _factory.Clock.Advance(TimeSpan.FromMinutes(10));

        using var response = await _client.GetAsync("/expired-quiet");
        await _factory.DrainClicksAsync();

        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        using var get = await _client.GetAsync("/api/links/expired-quiet");
        Assert.Equal(0, (await get.JsonAsync()).Num("clicks"));
    }

    [Fact]
    public async Task A_link_with_a_click_budget_answers_410_once_the_budget_is_spent()
    {
        await CurtoFactory.CreateLinkAsync(_client, "https://example.com/budget", body =>
        {
            body["slug"] = "budget-2";
            body["maxClicks"] = 2;
        });

        using var first = await _client.GetAsync("/budget-2");
        using var second = await _client.GetAsync("/budget-2");
        using var third = await _client.GetAsync("/budget-2");

        Assert.Equal(HttpStatusCode.Found, first.StatusCode);
        Assert.Equal(HttpStatusCode.Found, second.StatusCode);
        var problem = await third.ExpectProblemAsync(410);
        Assert.Equal("link_exhausted", problem.Str("code"));

        // The counter of a limited link is exact right away: no waiting for the background writer.
        using var get = await _client.GetAsync("/api/links/budget-2");
        var body = await get.JsonAsync();
        Assert.Equal(2, body.Num("clicks"));
        Assert.Equal("exhausted", body.Str("status"));

        await _factory.DrainClicksAsync();
        using var again = await _client.GetAsync("/api/links/budget-2");
        Assert.Equal(2, (await again.JsonAsync()).Num("clicks")); // the queued events are not double counted
    }

    [Fact]
    public async Task The_click_budget_holds_under_concurrency()
    {
        await CurtoFactory.CreateLinkAsync(_client, "https://example.com/race", body =>
        {
            body["slug"] = "race-budget";
            body["maxClicks"] = 5;
        });

        var statuses = await Task.WhenAll(Enumerable.Range(0, 40).Select(async _ =>
        {
            using var response = await _client.GetAsync("/race-budget");
            return response.StatusCode;
        }));

        Assert.Equal(5, statuses.Count(s => s == HttpStatusCode.Found));
        Assert.Equal(35, statuses.Count(s => s == HttpStatusCode.Gone));

        await _factory.DrainClicksAsync();
        using var get = await _client.GetAsync("/api/links/race-budget");
        Assert.Equal(5, (await get.JsonAsync()).Num("clicks"));
    }

    [Fact]
    public async Task Head_never_spends_the_budget_but_reports_an_exhausted_link()
    {
        await CurtoFactory.CreateLinkAsync(_client, "https://example.com/head", body =>
        {
            body["slug"] = "head-budget";
            body["maxClicks"] = 1;
        });

        using var head = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/head-budget"));
        using var visit = await _client.GetAsync("/head-budget");
        using var headAfter = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/head-budget"));

        Assert.Equal(HttpStatusCode.Found, head.StatusCode);
        Assert.Equal(HttpStatusCode.Found, visit.StatusCode);
        Assert.Equal(HttpStatusCode.Gone, headAfter.StatusCode);
    }

    [Fact]
    public async Task Expiry_wins_when_a_link_is_both_expired_and_exhausted()
    {
        await CurtoFactory.CreateLinkAsync(_client, "https://example.com/both", body =>
        {
            body["slug"] = "both-gone";
            body["maxClicks"] = 1;
            body["expiresAt"] = _factory.Clock.GetUtcNow().AddMinutes(1);
        });
        using var visit = await _client.GetAsync("/both-gone");
        _factory.Clock.Advance(TimeSpan.FromMinutes(2));

        using var response = await _client.GetAsync("/both-gone");

        var problem = await response.ExpectProblemAsync(410);
        Assert.Equal("link_expired", problem.Str("code"));
    }

    [Fact]
    public async Task Patching_a_new_expiry_or_budget_revives_the_link()
    {
        await CurtoFactory.CreateLinkAsync(_client, "https://example.com/revive", body =>
        {
            body["slug"] = "revive-me";
            body["expiresAt"] = _factory.Clock.GetUtcNow().AddMinutes(1);
        });
        _factory.Clock.Advance(TimeSpan.FromMinutes(5));
        using var gone = await _client.GetAsync("/revive-me");
        Assert.Equal(HttpStatusCode.Gone, gone.StatusCode);

        var newExpiry = _factory.Clock.GetUtcNow().AddDays(1).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        using var patched = await _client.SendJsonAsync(HttpMethod.Patch, "/api/links/revive-me", $$"""{"expiresAt":"{{newExpiry}}"}""");
        using var back = await _client.GetAsync("/revive-me");

        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        Assert.Equal("active", (await patched.JsonAsync()).Str("status"));
        Assert.Equal(HttpStatusCode.Found, back.StatusCode);
    }

    [Fact]
    public async Task Expiry_must_be_in_the_future_and_carry_an_offset()
    {
        var now = _factory.Clock.GetUtcNow();
        var cases = new Dictionary<string, object>
        {
            ["past"] = now.AddSeconds(-1),
            ["now"] = now,
            ["no-offset"] = "2031-01-31T12:00:00",
            ["date-only"] = "2031-01-31",
            ["garbage"] = "next tuesday",
            ["number"] = 1_900_000_000,
        };

        foreach (var (name, value) in cases)
        {
            using var response = await _client.PostAsJsonAsync("/api/links", new { url = "https://example.com", expiresAt = value });

            // A malformed timestamp fails while the body is read (400 without field detail); a past one fails validation.
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.True(
                response.Content.Headers.ContentType?.MediaType == "application/problem+json",
                $"{name}: not a problem document");
        }
    }

    [Fact]
    public async Task Expiry_in_the_past_names_the_field()
    {
        using var response = await _client.PostAsJsonAsync("/api/links", new { url = "https://example.com", expiresAt = _factory.Clock.GetUtcNow().AddDays(-1) });

        var problem = await response.ExpectProblemAsync(400);
        Assert.True(problem.GetProperty("errors").TryGetProperty("expiresAt", out _));
    }
}
