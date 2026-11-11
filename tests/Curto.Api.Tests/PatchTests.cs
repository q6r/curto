using System.Net;
using System.Text.Json;
using Curto.Api.Tests.Support;

namespace Curto.Api.Tests;

public sealed class PatchTests : IClassFixture<CurtoFactory>, IDisposable
{
    private readonly CurtoFactory _factory;
    private readonly HttpClient _client;

    public PatchTests(CurtoFactory factory)
    {
        _factory = factory;
        _client = factory.CreateApiClient();
    }

    public void Dispose() => _client.Dispose();

    /// <summary>Creates a fully populated link and returns its slug.</summary>
    private async Task<string> SeedAsync()
    {
        // xUnit builds a new test class instance per test, so a counter would restart: use a random suffix instead.
        var slug = "patch-" + Guid.NewGuid().ToString("N")[..12];
        await CurtoFactory.CreateLinkAsync(_client, "https://example.com/original", body =>
        {
            body["slug"] = slug;
            body["title"] = "Original";
            body["tags"] = new[] { "one", "two" };
            body["expiresAt"] = _factory.Clock.GetUtcNow().AddDays(30);
            body["maxClicks"] = 100;
        });
        return slug;
    }

    private async Task<JsonElement> PatchOkAsync(string slug, string json, string mediaType = "application/json")
    {
        using var response = await _client.SendJsonAsync(HttpMethod.Patch, $"/api/links/{slug}", json, mediaType);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.JsonAsync();
    }

    [Fact]
    public async Task Only_the_fields_present_change()
    {
        var slug = await SeedAsync();
        var before = await (await _client.GetAsync($"/api/links/{slug}")).JsonAsync();

        var after = await PatchOkAsync(slug, """{"title":"Renamed"}""");

        Assert.Equal("Renamed", after.Str("title"));
        foreach (var unchanged in new[] { "slug", "shortUrl", "url", "createdAt", "expiresAt", "maxClicks", "clicks", "status" })
        {
            Assert.Equal(before.GetProperty(unchanged).ToString(), after.GetProperty(unchanged).ToString());
        }

        Assert.Equal(before.Strings("tags"), after.Strings("tags"));
    }

    [Fact]
    public async Task Null_clears_the_optional_fields()
    {
        var slug = await SeedAsync();

        var after = await PatchOkAsync(slug, """{"title":null,"tags":null,"expiresAt":null,"maxClicks":null}""");

        Assert.True(after.IsNull("title"));
        Assert.Empty(after.Strings("tags"));
        Assert.True(after.IsNull("expiresAt"));
        Assert.True(after.IsNull("maxClicks"));
        Assert.Equal("https://example.com/original", after.Str("url"));
        Assert.Equal("active", after.Str("status"));

        // The change is persisted, not just echoed.
        var reread = await (await _client.GetAsync($"/api/links/{slug}")).JsonAsync();
        Assert.Equal(after.ToString(), reread.ToString());
    }

    [Fact]
    public async Task Values_are_validated_and_normalised_like_on_create()
    {
        var slug = await SeedAsync();

        var after = await PatchOkAsync(slug, """
            {"url":"HTTPS://Example.ORG/New Path?q=é","title":"  Spaced  ","tags":["B","b","A"],"expiresAt":"2031-01-31T12:00:00+02:00","maxClicks":7}
            """);

        Assert.Equal("https://example.org/New%20Path?q=%C3%A9", after.Str("url"));
        Assert.Equal("Spaced", after.Str("title"));
        Assert.Equal(["b", "a"], after.Strings("tags"));
        Assert.Equal("2031-01-31T10:00:00Z", after.Str("expiresAt"));
        Assert.Equal(7, after.Num("maxClicks"));
    }

    [Fact]
    public async Task A_patch_changes_where_the_redirect_goes()
    {
        var slug = await SeedAsync();

        await PatchOkAsync(slug, """{"url":"https://example.com/moved"}""");
        using var redirect = await _client.GetAsync($"/{slug}");

        Assert.Equal("https://example.com/moved", redirect.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Both_json_media_types_are_accepted()
    {
        var slug = await SeedAsync();

        var plain = await PatchOkAsync(slug, """{"title":"a"}""", "application/json");
        var mergePatch = await PatchOkAsync(slug, """{"title":"b"}""", "application/merge-patch+json");

        Assert.Equal("a", plain.Str("title"));
        Assert.Equal("b", mergePatch.Str("title"));
    }

    [Fact]
    public async Task An_empty_patch_is_a_valid_no_op()
    {
        var slug = await SeedAsync();
        var before = await (await _client.GetAsync($"/api/links/{slug}")).JsonAsync();

        var after = await PatchOkAsync(slug, "{}");

        Assert.Equal(before.ToString(), after.ToString());
    }

    [Fact]
    public async Task Clicks_and_creation_time_survive_a_patch()
    {
        var slug = await SeedAsync();
        for (var i = 0; i < 3; i++)
        {
            using var visit = await _client.GetAsync($"/{slug}");
        }

        await _factory.DrainClicksAsync();
        _factory.Clock.Advance(TimeSpan.FromMinutes(5));
        var after = await PatchOkAsync(slug, """{"title":"later"}""");

        Assert.Equal(3, after.Num("clicks"));
        Assert.NotEqual(_factory.Clock.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture), after.Str("createdAt"));
    }

    [Theory]
    [InlineData("""{"url":null}""", "url")]
    [InlineData("""{"url":42}""", "url")]
    [InlineData("""{"url":"javascript:alert(1)"}""", "url")]
    [InlineData("""{"url":"ftp://example.com/x"}""", "url")]
    [InlineData("""{"url":"https://user:pw@example.com/"}""", "url")]
    [InlineData("""{"title":42}""", "title")]
    [InlineData("""{"title":["a"]}""", "title")]
    [InlineData("""{"tags":"one"}""", "tags")]
    [InlineData("""{"tags":[1,2]}""", "tags")]
    [InlineData("""{"tags":["ok","not ok"]}""", "tags")]
    [InlineData("""{"expiresAt":"2020-01-01T00:00:00Z"}""", "expiresAt")]
    [InlineData("""{"expiresAt":"2031-01-01T00:00:00"}""", "expiresAt")]
    [InlineData("""{"expiresAt":12345}""", "expiresAt")]
    [InlineData("""{"maxClicks":0}""", "maxClicks")]
    [InlineData("""{"maxClicks":-1}""", "maxClicks")]
    [InlineData("""{"maxClicks":"3"}""", "maxClicks")]
    [InlineData("""{"maxClicks":2.5}""", "maxClicks")]
    [InlineData("""{"slug":"renamed-link"}""", "slug")]
    [InlineData("""{"clicks":0}""", "clicks")]
    [InlineData("""{"createdAt":"2020-01-01T00:00:00Z"}""", "createdAt")]
    [InlineData("""{"status":"active"}""", "status")]
    [InlineData("""{"surprise":true}""", "surprise")]
    public async Task Invalid_patches_name_the_offending_field_and_change_nothing(string json, string field)
    {
        var slug = await SeedAsync();
        var before = await (await _client.GetAsync($"/api/links/{slug}")).JsonAsync();

        using var response = await _client.SendJsonAsync(HttpMethod.Patch, $"/api/links/{slug}", json);

        var problem = await response.ExpectProblemAsync(400);
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), problem.ToString());
        var after = await (await _client.GetAsync($"/api/links/{slug}")).JsonAsync();
        Assert.Equal(before.ToString(), after.ToString());
    }

    [Fact]
    public async Task One_bad_field_rejects_the_whole_patch()
    {
        var slug = await SeedAsync();

        using var response = await _client.SendJsonAsync(HttpMethod.Patch, $"/api/links/{slug}", """{"title":"would be applied","maxClicks":0}""");

        await response.ExpectProblemAsync(400);
        var after = await (await _client.GetAsync($"/api/links/{slug}")).JsonAsync();
        Assert.Equal("Original", after.Str("title"));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    [InlineData("null")]
    public async Task A_patch_must_be_a_json_object(string json)
    {
        var slug = await SeedAsync();

        using var response = await _client.SendJsonAsync(HttpMethod.Patch, $"/api/links/{slug}", json);

        await response.ExpectProblemAsync(400);
    }

    [Fact]
    public async Task Malformed_json_is_a_400_and_a_wrong_media_type_a_415()
    {
        var slug = await SeedAsync();

        using var malformed = await _client.SendJsonAsync(HttpMethod.Patch, $"/api/links/{slug}", "{ nope");
        using var wrongType = await _client.SendJsonAsync(HttpMethod.Patch, $"/api/links/{slug}", """{"title":"x"}""", "text/plain");

        await malformed.ExpectProblemAsync(400);
        await wrongType.ExpectProblemAsync(415);
    }

    [Fact]
    public async Task Patching_an_unknown_link_is_a_404()
    {
        using var response = await _client.SendJsonAsync(HttpMethod.Patch, "/api/links/never-existed", """{"title":"x"}""");

        await response.ExpectProblemAsync(404);
    }

    [Fact]
    public async Task Concurrent_patches_of_different_fields_do_not_lose_updates()
    {
        var slug = await SeedAsync();

        var responses = await Task.WhenAll(
            _client.SendJsonAsync(HttpMethod.Patch, $"/api/links/{slug}", """{"title":"from-a"}"""),
            _client.SendJsonAsync(HttpMethod.Patch, $"/api/links/{slug}", """{"tags":["from-b"]}"""),
            _client.SendJsonAsync(HttpMethod.Patch, $"/api/links/{slug}", """{"maxClicks":55}"""),
            _client.SendJsonAsync(HttpMethod.Patch, $"/api/links/{slug}", """{"url":"https://example.com/from-d"}"""));

        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            response.Dispose();
        }

        var final = await (await _client.GetAsync($"/api/links/{slug}")).JsonAsync();
        Assert.Equal("from-a", final.Str("title"));
        Assert.Equal(["from-b"], final.Strings("tags"));
        Assert.Equal(55, final.Num("maxClicks"));
        Assert.Equal("https://example.com/from-d", final.Str("url"));
    }
}
