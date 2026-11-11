using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Curto.Api.Tests.Support;
using Microsoft.Data.Sqlite;

namespace Curto.Api.Tests;

public sealed class LinkLifecycleTests : IClassFixture<CurtoFactory>, IDisposable
{
    private readonly CurtoFactory _factory;
    private readonly HttpClient _client;

    public LinkLifecycleTests(CurtoFactory factory)
    {
        _factory = factory;
        _client = factory.CreateApiClient();
    }

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task Create_returns_201_with_the_documented_contract()
    {
        var expectedCreatedAt = _factory.Clock.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

        using var response = await _client.PostAsJsonAsync("/api/links", new { url = "https://example.com/page?x=1" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.JsonAsync();
        var slug = body.Str("slug");

        Assert.Equal(
            ["slug", "shortUrl", "url", "title", "tags", "createdAt", "expiresAt", "maxClicks", "clicks", "status"],
            body.Names());
        Assert.Matches("^[0-9A-Za-z]{7}$", slug);
        Assert.Equal($"/api/links/{slug}", response.Headers.Location!.ToString());
        Assert.Equal($"http://localhost/{slug}", body.Str("shortUrl"));
        Assert.Equal("https://example.com/page?x=1", body.Str("url"));
        Assert.True(body.IsNull("title"));
        Assert.Empty(body.Strings("tags"));
        Assert.Equal(expectedCreatedAt, body.Str("createdAt"));
        Assert.True(body.IsNull("expiresAt"));
        Assert.True(body.IsNull("maxClicks"));
        Assert.Equal(0, body.Num("clicks"));
        Assert.Equal("active", body.Str("status"));
    }

    [Fact]
    public async Task Create_accepts_every_optional_field()
    {
        var link = await CurtoFactory.CreateLinkAsync(_client, "https://example.com/full", body =>
        {
            body["slug"] = "full-featured";
            body["title"] = "  A title  ";
            body["tags"] = new[] { "Marketing", "marketing", " Q1-2026 " };
            body["expiresAt"] = "2031-01-31T12:00:00-03:00";
            body["maxClicks"] = 500;
        });

        Assert.Equal("full-featured", link.Str("slug"));
        Assert.Equal("A title", link.Str("title"));
        Assert.Equal(["marketing", "q1-2026"], link.Strings("tags"));
        Assert.Equal("2031-01-31T15:00:00Z", link.Str("expiresAt"));
        Assert.Equal(500, link.Num("maxClicks"));
    }

    [Fact]
    public async Task Redirect_answers_302_with_no_store_and_the_click_is_counted_afterwards()
    {
        var link = await CurtoFactory.CreateLinkAsync(_client, "https://example.com/target?a=1&b=%C3%A9");
        var slug = link.Str("slug");

        using var response = await _client.GetAsync($"/{slug}");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("https://example.com/target?a=1&b=%C3%A9", response.Headers.Location!.OriginalString); // the exact wire value
        Assert.True(response.Headers.CacheControl!.NoStore);

        await _factory.DrainClicksAsync();
        using var after = await _client.GetAsync($"/api/links/{slug}");
        Assert.Equal(1, (await after.JsonAsync()).Num("clicks"));
    }

    [Fact]
    public async Task Every_visit_is_counted_and_the_redirect_never_waits_for_the_database_write()
    {
        var link = await CurtoFactory.CreateLinkAsync(_client);
        var slug = link.Str("slug");

        for (var i = 0; i < 25; i++)
        {
            using var response = await _client.GetAsync($"/{slug}");
            Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        }

        await _factory.DrainClicksAsync();
        using var after = await _client.GetAsync($"/api/links/{slug}");
        Assert.Equal(25, (await after.JsonAsync()).Num("clicks"));
    }

    [Fact]
    public async Task Head_requests_are_link_checks_and_do_not_count()
    {
        var link = await CurtoFactory.CreateLinkAsync(_client, "https://example.com/head");
        var slug = link.Str("slug");

        using var head = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/{slug}"));
        using var unknown = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/nosuchslug"));

        Assert.Equal(HttpStatusCode.Found, head.StatusCode);
        Assert.Equal("https://example.com/head", head.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        await _factory.DrainClicksAsync();
        using var after = await _client.GetAsync($"/api/links/{slug}");
        Assert.Equal(0, (await after.JsonAsync()).Num("clicks"));
    }

    [Fact]
    public async Task Unknown_slug_is_a_404_problem_and_nothing_is_recorded()
    {
        using var response = await _client.GetAsync("/nosuchslug");

        var problem = await response.ExpectProblemAsync(404);
        Assert.Contains("nosuchslug", problem.Str("detail"), StringComparison.Ordinal);
        Assert.Equal("/nosuchslug", problem.Str("instance"));
        Assert.True(response.Headers.CacheControl!.NoStore);
    }

    [Fact]
    public async Task Slugs_are_case_sensitive()
    {
        await CurtoFactory.CreateLinkAsync(_client, "https://example.com/case", body => body["slug"] = "MixedCase");

        using var exact = await _client.GetAsync("/MixedCase");
        using var lower = await _client.GetAsync("/mixedcase");

        Assert.Equal(HttpStatusCode.Found, exact.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, lower.StatusCode);
    }

    [Fact]
    public async Task Slugs_that_could_never_exist_are_rejected_without_touching_the_database()
    {
        // Too short, too long, dots and encoded separators never match the route constraint.
        foreach (var path in new[] { "/ab", "/" + new string('a', 33), "/robots.txt", "/favicon.ico", "/a%20b", "/caf%C3%A9" })
        {
            using var response = await _client.GetAsync(path);
            await response.ExpectProblemAsync(404);
        }
    }

    [Fact]
    public async Task Get_returns_the_link_and_404_problem_for_unknown_slugs()
    {
        var link = await CurtoFactory.CreateLinkAsync(_client, "https://example.com/get", body => body["slug"] = "get-me-123");

        using var found = await _client.GetAsync("/api/links/get-me-123");
        using var missing = await _client.GetAsync("/api/links/missing-123");

        Assert.Equal(HttpStatusCode.OK, found.StatusCode);
        Assert.Equal(link.ToString(), (await found.JsonAsync()).ToString());
        await missing.ExpectProblemAsync(404);
    }

    [Fact]
    public async Task Delete_removes_the_link_its_clicks_and_the_redirect()
    {
        var link = await CurtoFactory.CreateLinkAsync(_client, "https://example.com/delete", body => body["slug"] = "delete-me-1");
        using var visit = await _client.GetAsync("/delete-me-1");
        await _factory.DrainClicksAsync();
        Assert.Equal(1, await CountClicksAsync("delete-me-1"));

        using var deleted = await _client.DeleteAsync("/api/links/delete-me-1");
        using var again = await _client.DeleteAsync("/api/links/delete-me-1");
        using var redirect = await _client.GetAsync("/delete-me-1");
        using var get = await _client.GetAsync("/api/links/delete-me-1");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Empty(await deleted.Content.ReadAsStringAsync());
        await again.ExpectProblemAsync(404);
        await redirect.ExpectProblemAsync(404);
        await get.ExpectProblemAsync(404);
        Assert.Equal(0, await CountClicksAsync("delete-me-1"));
        Assert.Equal("delete-me-1", link.Str("slug"));
    }

    [Fact]
    public async Task A_click_that_was_queued_for_a_link_deleted_meanwhile_is_dropped_quietly()
    {
        var link = await CurtoFactory.CreateLinkAsync(_client, "https://example.com/race", body => body["slug"] = "race-link-1");
        var survivor = await CurtoFactory.CreateLinkAsync(_client, "https://example.com/survivor", body => body["slug"] = "survivor-1");

        using var visit = await _client.GetAsync("/race-link-1");
        using var survivorVisit = await _client.GetAsync("/survivor-1");
        using var deleted = await _client.DeleteAsync("/api/links/race-link-1");
        await _factory.DrainClicksAsync();

        Assert.Equal(0, await CountClicksAsync("race-link-1"));
        Assert.Equal(1, await CountClicksAsync("survivor-1"));
        Assert.Equal("race-link-1", link.Str("slug"));
        Assert.Equal("survivor-1", survivor.Str("slug"));
    }

    [Fact]
    public async Task List_filters_sorts_and_pages()
    {
        var tag = "list-" + Guid.NewGuid().ToString("N")[..8];
        foreach (var (slug, title) in new[] { ("list-aaa", "Alpha release"), ("list-bbb", "Beta release"), ("list-ccc", "Gamma notes") })
        {
            await CurtoFactory.CreateLinkAsync(_client, $"https://example.com/{slug}", body =>
            {
                body["slug"] = slug;
                body["title"] = title;
                body["tags"] = new[] { tag };
            });
            _factory.Clock.Advance(TimeSpan.FromSeconds(1)); // distinct creation times
        }

        // Clicks: aaa 0, bbb 2, ccc 1.
        foreach (var slug in new[] { "list-bbb", "list-bbb", "list-ccc" })
        {
            using var visit = await _client.GetAsync($"/{slug}");
        }

        await _factory.DrainClicksAsync();

        Assert.Equal(["list-ccc", "list-bbb", "list-aaa"], await SlugsAsync($"tag={tag}"));
        Assert.Equal(["list-aaa", "list-bbb", "list-ccc"], await SlugsAsync($"tag={tag}&sort=createdAt&dir=asc"));
        Assert.Equal(["list-bbb", "list-ccc", "list-aaa"], await SlugsAsync($"tag={tag}&sort=clicks&dir=desc"));
        Assert.Equal(["list-aaa", "list-ccc", "list-bbb"], await SlugsAsync($"tag={tag}&sort=clicks&dir=asc"));
        Assert.Equal(["list-aaa", "list-bbb"], await SlugsAsync($"tag={tag}&query=release&sort=createdAt&dir=asc"));
        Assert.Equal(["list-bbb"], await SlugsAsync($"tag={tag}&query=BETA"));
        Assert.Equal(["list-ccc"], await SlugsAsync($"tag={tag}&query=list-ccc"));
        Assert.Equal(["list-bbb"], await SlugsAsync($"tag={tag}&sort=createdAt&dir=asc&limit=1&offset=1"));
        Assert.Empty(await SlugsAsync($"tag={tag}&query=nomatch"));
        Assert.Empty(await SlugsAsync("tag=no-such-tag"));

        using var page = await _client.GetAsync($"/api/links?tag={tag}&limit=2");
        var body = await page.JsonAsync();
        Assert.Equal(["items", "total"], body.Names());
        Assert.Equal(3, body.Num("total"));
        Assert.Equal(2, body.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task List_filters_by_status()
    {
        var tag = "status-" + Guid.NewGuid().ToString("N")[..8];
        var soon = _factory.Clock.GetUtcNow().AddMinutes(30);
        await CurtoFactory.CreateLinkAsync(_client, "https://example.com/s1", body => { body["slug"] = "stat-active"; body["tags"] = new[] { tag }; });
        await CurtoFactory.CreateLinkAsync(_client, "https://example.com/s2", body => { body["slug"] = "stat-expiring"; body["tags"] = new[] { tag }; body["expiresAt"] = soon; });
        await CurtoFactory.CreateLinkAsync(_client, "https://example.com/s3", body => { body["slug"] = "stat-budget"; body["tags"] = new[] { tag }; body["maxClicks"] = 1; });
        using var spend = await _client.GetAsync("/stat-budget");

        Assert.Equal(["stat-active", "stat-budget", "stat-expiring"], (await SlugsAsync($"tag={tag}&sort=createdAt&dir=asc")).Order(StringComparer.Ordinal));
        Assert.Equal(["stat-active", "stat-expiring"], (await SlugsAsync($"tag={tag}&status=active")).Order(StringComparer.Ordinal));
        Assert.Equal(["stat-budget"], await SlugsAsync($"tag={tag}&status=exhausted"));
        Assert.Empty(await SlugsAsync($"tag={tag}&status=expired"));

        _factory.Clock.Advance(TimeSpan.FromHours(1));

        Assert.Equal(["stat-expiring"], await SlugsAsync($"tag={tag}&status=expired"));
        Assert.Equal(["stat-active"], await SlugsAsync($"tag={tag}&status=active"));
        Assert.Equal(["stat-budget"], await SlugsAsync($"tag={tag}&status=exhausted"));
    }

    [Fact]
    public async Task List_text_search_treats_like_wildcards_literally()
    {
        var tag = "like-" + Guid.NewGuid().ToString("N")[..8];
        await CurtoFactory.CreateLinkAsync(_client, "https://example.com/l1", body => { body["slug"] = "like-one"; body["title"] = "100% legit"; body["tags"] = new[] { tag }; });
        await CurtoFactory.CreateLinkAsync(_client, "https://example.com/l2", body => { body["slug"] = "like-two"; body["title"] = "100x legit"; body["tags"] = new[] { tag }; });
        await CurtoFactory.CreateLinkAsync(_client, "https://example.com/l3", body => { body["slug"] = "like-three"; body["title"] = "a_b"; body["tags"] = new[] { tag }; });
        await CurtoFactory.CreateLinkAsync(_client, "https://example.com/l4", body => { body["slug"] = "like-four"; body["title"] = "axb"; body["tags"] = new[] { tag }; });

        Assert.Equal(["like-one"], await SlugsAsync($"tag={tag}&query=100%25"));
        Assert.Equal(["like-three"], await SlugsAsync($"tag={tag}&query=a_b"));
        Assert.Equal(["like-two"], await SlugsAsync($"tag={tag}&query=100x"));
    }

    [Theory]
    [InlineData("status=bogus", "status")]
    [InlineData("status=1", "status")]
    [InlineData("sort=name", "sort")]
    [InlineData("dir=up", "dir")]
    [InlineData("limit=0", "limit")]
    [InlineData("limit=201", "limit")]
    [InlineData("offset=-1", "offset")]
    public async Task List_rejects_bad_parameters_with_field_errors(string query, string field)
    {
        using var response = await _client.GetAsync($"/api/links?{query}");

        var problem = await response.ExpectProblemAsync(400);
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out var messages));
        Assert.NotEmpty(messages.EnumerateArray());
    }

    [Fact]
    public async Task List_rejects_values_that_are_not_numbers()
    {
        using var response = await _client.GetAsync("/api/links?limit=many");

        await response.ExpectProblemAsync(400);
    }

    [Fact]
    public async Task Tags_are_trimmed_lower_cased_and_deduplicated()
    {
        var link = await CurtoFactory.CreateLinkAsync(_client, "https://example.com/tags", body => body["tags"] = new[] { "Foo", "foo", " BAR ", "Café", "a:b.c_d-e" });

        Assert.Equal(["foo", "bar", "café", "a:b.c_d-e"], link.Strings("tags"));
    }

    [Fact]
    public async Task Invalid_input_is_reported_per_field()
    {
        var response = await _client.PostAsJsonAsync("/api/links", new
        {
            url = "ftp://example.com",
            slug = "x",
            title = new string('t', 201),
            tags = Enumerable.Range(0, 11).Select(i => "tag" + i).ToArray(),
            maxClicks = 0,
        });

        var problem = await response.ExpectProblemAsync(400);
        var errors = problem.GetProperty("errors");
        Assert.Equal(["url", "slug", "title", "tags", "maxClicks"], errors.Names());
    }

    [Theory]
    [InlineData("""{"url":"https://example.com","tags":["bad tag"]}""", "tags")]
    [InlineData("""{"url":"https://example.com","tags":["a/b"]}""", "tags")]
    [InlineData("""{"url":"https://example.com","tags":[""]}""", "tags")]
    [InlineData("""{"url":"https://example.com","title":"bad\u0007title"}""", "title")]
    [InlineData("""{"url":"https://example.com","maxClicks":-5}""", "maxClicks")]
    [InlineData("""{"slug":"no-url-here"}""", "url")]
    [InlineData("""{"url":null}""", "url")]
    [InlineData("""{"url":""}""", "url")]
    public async Task Create_validation_messages(string body, string field)
    {
        using var response = await _client.SendJsonAsync(HttpMethod.Post, "/api/links", body);

        var problem = await response.ExpectProblemAsync(400);
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), problem.ToString());
    }

    [Fact]
    public async Task Search_text_and_filters_do_not_allow_sql_injection()
    {
        await CurtoFactory.CreateLinkAsync(_client, "https://example.com/inj", body => body["slug"] = "inj-target");

        // Whatever the filter says, the statement stays a constant: no rows come back, nothing is dropped.
        using var response = await _client.GetAsync("/api/links?query=%27%3B%20DROP%20TABLE%20links%3B--&tag=%27%20OR%20%271%27%3D%271");
        using var still = await _client.GetAsync("/api/links/inj-target");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, (await response.JsonAsync()).Num("total"));
        Assert.Equal(HttpStatusCode.OK, still.StatusCode);
    }

    private async Task<string[]> SlugsAsync(string query)
    {
        using var response = await _client.GetAsync($"/api/links?{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await response.JsonAsync()).GetProperty("items").EnumerateArray().Select(item => item.Str("slug"))];
    }

    private async Task<long> CountClicksAsync(string slug)
    {
        await using var connection = new SqliteConnection($"Data Source={_factory.DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM clicks WHERE slug = $slug";
        command.Parameters.AddWithValue("$slug", slug);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
