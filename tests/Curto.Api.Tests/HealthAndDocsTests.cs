using System.Net;
using Curto.Api.Tests.Support;
using Microsoft.Data.Sqlite;

namespace Curto.Api.Tests;

public sealed class HealthAndDocsTests : IClassFixture<CurtoFactory>, IDisposable
{
    private readonly CurtoFactory _factory;
    private readonly HttpClient _client;

    public HealthAndDocsTests(CurtoFactory factory)
    {
        _factory = factory;
        _client = factory.CreateApiClient(withKey: false);
    }

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task Liveness_says_ok_without_touching_the_database()
    {
        using var response = await _client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.JsonAsync();
        Assert.Equal(["status", "version"], body.Names());
        Assert.Equal("ok", body.Str("status"));
        Assert.Matches(@"^\d+\.\d+\.\d+", body.Str("version"));
    }

    [Fact]
    public async Task Readiness_checks_the_database_and_reports_the_click_pipeline()
    {
        using var authenticated = _factory.CreateApiClient();
        await CurtoFactory.CreateLinkAsync(authenticated, "https://example.com/ready", body => body["slug"] = "ready-link");
        using var visit = await _client.GetAsync("/ready-link");
        await _factory.DrainClicksAsync();

        using var response = await _client.GetAsync("/readyz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var body = await response.JsonAsync();
        Assert.Equal(["status", "version", "database", "clickQueue", "liveSubscribers", "slowConsumersDropped"], body.Names());
        Assert.Equal("ready", body.Str("status"));
        Assert.Equal("ok", body.Str("database"));

        var queue = body.GetProperty("clickQueue");
        Assert.Equal(["depth", "capacity", "enqueued", "persisted", "dropped", "failed"], queue.Names());
        Assert.Equal(0, queue.Num("depth"));
        Assert.Equal(10_000, queue.Num("capacity"));
        Assert.Equal(1, queue.Num("enqueued"));
        Assert.Equal(1, queue.Num("persisted"));
        Assert.Equal(0, queue.Num("dropped")); // the drop-oldest counter is part of the contract
        Assert.Equal(0, queue.Num("failed"));
        Assert.Equal(0, body.Num("liveSubscribers"));
    }

    [Fact]
    public async Task Readiness_fails_with_a_503_problem_when_the_database_is_unusable()
    {
        await using var factory = new CurtoFactory();
        using var client = factory.CreateClient();
        using (var ok = await client.GetAsync("/readyz"))
        {
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        // Break the schema underneath the running application.
        await using (var connection = new SqliteConnection($"Data Source={factory.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE clicks; DROP TABLE links;";
            await command.ExecuteNonQueryAsync();
        }

        using var response = await client.GetAsync("/readyz");

        var problem = await response.ExpectProblemAsync(503);
        Assert.Contains("database", problem.Str("detail"), StringComparison.OrdinalIgnoreCase);
        Assert.True(response.Headers.Contains("Retry-After"));

        // Liveness is unaffected: the process is up, it just cannot serve.
        using var live = await client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }

    [Fact]
    public async Task The_database_uses_wal_and_the_schema_version_is_recorded()
    {
        using var warmup = await _client.GetAsync("/healthz");

        await using var connection = new SqliteConnection($"Data Source={_factory.DatabasePath}");
        await connection.OpenAsync();
        await using var mode = connection.CreateCommand();
        mode.CommandText = "PRAGMA journal_mode;";
        await using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version;";

        Assert.Equal("wal", (string)(await mode.ExecuteScalarAsync())!);
        Assert.Equal(1L, (long)(await version.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task The_openapi_document_describes_the_whole_contract()
    {
        using var response = await _client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = await response.JsonAsync();
        Assert.Equal("Curto API", document.GetProperty("info").Str("title"));

        var paths = document.GetProperty("paths");
        string[] expected =
        [
            "/api/links", "/api/links/{slug}", "/api/links/{slug}/stats", "/api/stats/overview", "/api/events",
            "/{slug}", "/qr/{slug}.svg", "/healthz", "/readyz",
        ];
        foreach (var path in expected)
        {
            Assert.True(paths.TryGetProperty(path, out _), $"missing path {path}; have: {string.Join(", ", paths.Names())}");
        }

        Assert.DoesNotContain(paths.Names(), p => p.StartsWith("/docs", StringComparison.Ordinal) || p.StartsWith("/openapi", StringComparison.Ordinal));
        Assert.Equal(["get", "post"], paths.GetProperty("/api/links").Names().Order().ToArray());
        Assert.Equal(["delete", "get", "patch"], paths.GetProperty("/api/links/{slug}").Names().Order().ToArray());
    }

    [Fact]
    public async Task The_openapi_document_declares_the_api_key_scheme_on_management_operations_only()
    {
        using var response = await _client.GetAsync("/openapi/v1.json");
        var document = await response.JsonAsync();

        var scheme = document.GetProperty("components").GetProperty("securitySchemes").GetProperty("ApiKey");
        Assert.Equal("apiKey", scheme.Str("type"));
        Assert.Equal("header", scheme.Str("in"));
        Assert.Equal("X-API-Key", scheme.Str("name"));

        var paths = document.GetProperty("paths");
        foreach (var path in new[] { "/api/links", "/api/links/{slug}", "/api/links/{slug}/stats", "/api/stats/overview", "/api/events" })
        {
            foreach (var operation in paths.GetProperty(path).EnumerateObject())
            {
                Assert.True(operation.Value.TryGetProperty("security", out var security), $"{operation.Name} {path} has no security requirement");
                Assert.Contains("ApiKey", security.ToString(), StringComparison.Ordinal);
            }
        }

        foreach (var path in new[] { "/healthz", "/readyz", "/{slug}", "/qr/{slug}.svg" })
        {
            foreach (var operation in paths.GetProperty(path).EnumerateObject())
            {
                Assert.False(operation.Value.TryGetProperty("security", out _), $"{operation.Name} {path} is public");
            }
        }
    }

    [Fact]
    public async Task The_openapi_document_does_not_depend_on_the_caller()
    {
        using var first = await _client.GetAsync("/openapi/v1.json");
        using var second = await _client.GetAsync("/openapi/v1.json");

        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_api_reference_page_loads_its_script_from_this_origin_with_a_nonce()
    {
        using var response = await _client.GetAsync("/docs/v1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        var nonce = System.Text.RegularExpressions.Regex.Match(csp, "'nonce-([^']+)'").Groups[1].Value;

        Assert.NotEmpty(nonce);
        Assert.DoesNotContain("http://", html.Replace("http://www.w3.org", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.DoesNotContain("https://", html, StringComparison.Ordinal);
        Assert.Contains("src=\"scalar.js\"", html, StringComparison.Ordinal);

        // The page may HTML-encode characters of the nonce (a '+' becomes &#x2B;); the browser decodes them before comparing.
        var inPage = System.Text.RegularExpressions.Regex.Matches(html, "<script[^>]*\\snonce=\"([^\"]+)\"").Select(m => System.Net.WebUtility.HtmlDecode(m.Groups[1].Value)).ToArray();
        Assert.NotEmpty(inPage);
        Assert.All(inPage, value => Assert.Equal(nonce, value));
        Assert.Contains("\"telemetry\":false", html, StringComparison.Ordinal);
        Assert.Contains("\"withDefaultFonts\":false", html, StringComparison.Ordinal);
        Assert.True(response.Headers.CacheControl!.NoStore);
    }

    [Fact]
    public async Task The_api_reference_bundle_is_served_locally()
    {
        using var response = await _client.GetAsync("/docs/scalar.js");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Content.Headers.ContentLength > 100_000);
    }

    [Fact]
    public async Task The_root_serves_the_static_index_and_static_files_are_not_swallowed_by_the_slug_route()
    {
        using var root = await _client.GetAsync("/");
        using var index = await _client.GetAsync("/index.html");

        Assert.Equal(HttpStatusCode.OK, root.StatusCode);
        Assert.Equal("text/html", root.Content.Headers.ContentType?.MediaType);
        Assert.Contains("curto", await root.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, index.StatusCode);
        Assert.Equal("no-cache", index.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task Unknown_files_are_a_404_problem_not_a_redirect_lookup()
    {
        using var response = await _client.GetAsync("/no-such-file.js");

        await response.ExpectProblemAsync(404);
    }

    [Fact]
    public async Task The_server_does_not_advertise_itself()
    {
        using var response = await _client.GetAsync("/healthz");

        Assert.False(response.Headers.Contains("X-Powered-By"));
        Assert.False(response.Headers.Contains("X-AspNet-Version"));
    }
}
