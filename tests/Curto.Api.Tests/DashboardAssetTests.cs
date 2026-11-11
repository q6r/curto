using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Curto.Api.Infrastructure;
using Curto.Api.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Curto.Api.Tests;

/// <summary>
/// The static layer: the dashboard in <c>wwwroot</c> is served with the strict policy, the right content types and
/// revalidating cache headers, and it contains nothing the policy would block (inline script or style) or that would
/// phone home (an external origin). These are the same rules the browser enforces, checked without a browser.
/// </summary>
public sealed partial class DashboardAssetTests : IClassFixture<CurtoFactory>, IDisposable
{
    /// <summary>Everything the dashboard ships, and nothing else: a stray file in wwwroot is published as well.</summary>
    private static readonly string[] ExpectedFiles =
        ["app.css", "app.js", "aurora-charts.js", "aurora.css", "aurora.js", "favicon.svg", "index.html"];

    private static readonly Dictionary<string, string[]> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".css"] = ["text/css"],
        [".html"] = ["text/html"],
        [".js"] = ["text/javascript", "application/javascript"],
        [".svg"] = ["image/svg+xml"],
    };

    // XML namespace identifiers are names, not requests; example hosts are the reserved documentation domains (RFC 2606).
    private static readonly string[] AllowedHosts = ["www.w3.org", "example.com", "www.example.com", "example.org", "example.net"];

    private readonly CurtoFactory _factory;
    private readonly HttpClient _client;

    public DashboardAssetTests(CurtoFactory factory)
    {
        _factory = factory;
        _client = factory.CreateApiClient(withKey: false);
    }

    public void Dispose() => _client.Dispose();

    private string WebRoot => _factory.Services.GetRequiredService<IWebHostEnvironment>().WebRootPath;

    private string[] Files() =>
    [
        .. Directory.EnumerateFiles(WebRoot, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(WebRoot, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal),
    ];

    private string Read(string file) => File.ReadAllText(Path.Combine(WebRoot, file));

    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    public async Task The_dashboard_is_served_without_a_key_under_the_strict_policy(string path)
    {
        using var response = await _client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(SecurityHeadersMiddleware.DefaultPolicy, response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.True(response.Headers.CacheControl?.NoCache);

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("<title>curto</title>", html, StringComparison.Ordinal);
        Assert.Contains("src=\"app.js\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void The_dashboard_ships_exactly_the_expected_files()
    {
        // No source maps, backups, fonts or images that nothing references.
        Assert.Equal(ExpectedFiles, Files());
    }

    [Fact]
    public async Task Every_file_is_served_with_its_content_type_and_revalidating_headers()
    {
        List<string> problems = [];

        foreach (var file in Files())
        {
            using var response = await _client.GetAsync("/" + file);

            if (response.StatusCode != HttpStatusCode.OK)
            {
                problems.Add($"/{file} answered {(int)response.StatusCode}");
                continue;
            }

            var type = response.Content.Headers.ContentType?.MediaType;
            if (!ContentTypes.TryGetValue(Path.GetExtension(file), out var accepted) || type is null || !accepted.Contains(type))
            {
                problems.Add($"/{file} is served as {type}");
            }

            if (response.Headers.CacheControl?.NoCache != true)
            {
                problems.Add($"/{file} must send Cache-Control: no-cache so an upgrade never serves a stale dashboard");
            }

            if (response.Headers.ETag is null)
            {
                problems.Add($"/{file} has no ETag, so revalidation would transfer the whole file");
            }

            if (!response.Headers.TryGetValues("Content-Security-Policy", out var csp) || csp.Single() != SecurityHeadersMiddleware.DefaultPolicy)
            {
                problems.Add($"/{file} is missing the strict Content-Security-Policy");
            }

            if (response.Headers.GetValues("X-Content-Type-Options").SingleOrDefault() != "nosniff")
            {
                problems.Add($"/{file} is missing X-Content-Type-Options: nosniff");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public async Task Revalidating_an_unchanged_file_costs_a_304()
    {
        using var first = await _client.GetAsync("/app.js");
        var etag = first.Headers.ETag;
        Assert.NotNull(etag);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/app.js");
        request.Headers.IfNoneMatch.Add(etag);
        using var second = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.True(second.Headers.CacheControl?.NoCache);
    }

    [Theory]
    [InlineData("/appsettings.json")]
    [InlineData("/%2e%2e/appsettings.json")]
    [InlineData("/..%2fappsettings.json")]
    [InlineData("/%2e%2e%2fCurto.Api.csproj")]
    [InlineData("/wwwroot/app.js")]
    public async Task Files_outside_wwwroot_are_not_reachable(string path)
    {
        using var response = await _client.GetAsync(path);

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("ConnectionStrings", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_page_falls_back_for_browsers_without_script_and_leaves_its_landmarks_to_the_shell()
    {
        var html = Read("index.html");

        Assert.Contains("<html lang=\"en\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"viewport\"", html, StringComparison.Ordinal);
        Assert.Contains("<body class=\"a-body\">", html, StringComparison.Ordinal);
        Assert.Contains("<noscript>", html, StringComparison.Ordinal);

        // The page is empty on purpose: Aurora.shell() builds the skip link, the rail, the breadcrumbs, <main> and the
        // status bar. A second <main>, <nav> or <header> in the HTML would be a duplicate landmark.
        Assert.DoesNotContain("<main", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<nav", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<header", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<footer", html, StringComparison.Ordinal);

        var script = Read("app.js");
        Assert.Contains("Aurora.shell({", script, StringComparison.Ordinal);
        Assert.Contains("shell.container", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_rail_destination_has_a_view_and_every_view_has_a_rail_destination()
    {
        var script = Read("app.js");

        var destinations = ItemId().Matches(RouteTable().Match(script).Groups["body"].Value).Select(m => m.Groups["id"].Value).Order(StringComparer.Ordinal).ToArray();
        var views = ViewKey().Matches(ViewFactories().Match(script).Groups["body"].Value).Select(m => m.Groups["key"].Value).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(["links", "live", "overview"], destinations);
        Assert.Equal(destinations, views);
    }

    [Fact]
    public async Task The_page_loads_only_local_files_and_loads_the_ui_kit_before_the_app()
    {
        var html = Read("index.html");

        var scripts = ScriptSource().Matches(html).Select(m => m.Groups["src"].Value).ToArray();
        Assert.Equal(["aurora.js", "aurora-charts.js", "app.js"], scripts);

        foreach (var reference in Reference().Matches(html).Select(m => m.Groups["url"].Value).Where(url => !url.StartsWith('#')))
        {
            Assert.False(reference.Contains(':', StringComparison.Ordinal) || reference.StartsWith("//", StringComparison.Ordinal), $"{reference} is not a same-origin reference");

            using var response = await _client.GetAsync(reference.StartsWith('/') ? reference : "/" + reference);
            Assert.True((int)response.StatusCode < 400, $"{reference} answered {(int)response.StatusCode}");
        }
    }

    [Fact]
    public void The_html_has_no_inline_script_style_or_event_handlers()
    {
        var html = Read("index.html");

        Assert.Empty(InlineScript().Matches(html));
        Assert.Empty(StyleElement().Matches(html));
        Assert.Empty(StyleAttribute().Matches(html));
        Assert.Empty(EventHandlerAttribute().Matches(html));
        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void No_asset_refers_to_an_external_origin()
    {
        List<string> problems = [];

        foreach (var file in Files())
        {
            var text = Read(file);

            foreach (Match match in AbsoluteUrl().Matches(text))
            {
                if (!AllowedHosts.Contains(match.Groups["host"].Value, StringComparer.OrdinalIgnoreCase))
                {
                    problems.Add($"{file}: {match.Value}");
                }
            }

            if (text.Contains("@import", StringComparison.Ordinal))
            {
                problems.Add($"{file}: @import pulls in another stylesheet");
            }
        }

        Assert.True(problems.Count == 0, "External origins would break offline use and the policy: " + string.Join("; ", problems));
    }

    [Fact]
    public void The_app_script_never_builds_markup_or_inline_styles()
    {
        var script = Read("app.js");

        foreach (var forbidden in new[] { "innerHTML", "outerHTML", "insertAdjacentHTML", "document.write", "javascript:", ".cssText", "new Function" })
        {
            Assert.DoesNotContain(forbidden, script, StringComparison.Ordinal);
        }

        Assert.Empty(EvalCall().Matches(script));
        Assert.Empty(StyleAttributeCall().Matches(script));
    }

    [Fact]
    public void The_app_stylesheet_uses_ui_kit_tokens_only()
    {
        var css = Read("app.css");

        Assert.Empty(ColourLiteral().Matches(css));
        Assert.DoesNotContain("url(", css, StringComparison.Ordinal);
        Assert.DoesNotContain("@font-face", css, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_api_path_the_dashboard_calls_exists_in_the_openapi_document()
    {
        using var response = await _client.GetAsync("/openapi/v1.json");
        var document = await response.JsonAsync();
        var documented = document.GetProperty("paths").EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        var called = ApiPath().Matches(Read("app.js"))
            .Select(m => TemplateExpression().Replace(m.Groups["path"].Value, "{slug}"))
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(called);
        Assert.True(called.All(documented.Contains), "Not in the OpenAPI document: " + string.Join(", ", called.Where(path => !documented.Contains(path))));
    }

    [Fact]
    public void The_ui_kit_files_are_the_bundled_aurora_runtime_with_the_shell()
    {
        Assert.StartsWith("/*! Aurora UI kit", Read("aurora.css"), StringComparison.Ordinal);
        Assert.Contains("root.Aurora", Read("aurora.js"), StringComparison.Ordinal);
        Assert.Contains("root.AuroraCharts", Read("aurora-charts.js"), StringComparison.Ordinal);

        // The dashboard is built by Aurora.shell(): an older copy of the kit without it would leave the page blank.
        Assert.Contains("Aurora.shell = shell", Read("aurora.js"), StringComparison.Ordinal);
        Assert.Contains(".a-rail", Read("aurora.css"), StringComparison.Ordinal);
    }

    [GeneratedRegex("(?:src|href)=\"(?<url>[^\"]+)\"")]
    private static partial Regex Reference();

    [GeneratedRegex("<script\\b[^>]*\\bsrc=\"(?<src>[^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptSource();

    [GeneratedRegex("<script\\b(?![^>]*\\bsrc\\s*=)[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex InlineScript();

    [GeneratedRegex("<style\\b", RegexOptions.IgnoreCase)]
    private static partial Regex StyleElement();

    [GeneratedRegex("\\sstyle\\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex StyleAttribute();

    [GeneratedRegex("\\son[a-z]+\\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex EventHandlerAttribute();

    [GeneratedRegex("(?:https?:)?//(?<host>[A-Za-z0-9-]+(?:\\.[A-Za-z0-9-]+)+)[^\\s\"'`)<>]*")]
    private static partial Regex AbsoluteUrl();

    [GeneratedRegex("\\beval\\s*\\(")]
    private static partial Regex EvalCall();

    [GeneratedRegex("setAttribute\\(\\s*['\"]style['\"]")]
    private static partial Regex StyleAttributeCall();

    [GeneratedRegex("#[0-9a-fA-F]{3,8}\\b|\\brgba?\\(|\\bhsla?\\(")]
    private static partial Regex ColourLiteral();

    [GeneratedRegex("['\"`](?<path>/(?:api|qr)/[^'\"`?]*|/healthz)")]
    private static partial Regex ApiPath();

    [GeneratedRegex("\\$\\{[^}]*\\}")]
    private static partial Regex TemplateExpression();

    [GeneratedRegex("const ROUTES = \\[(?<body>.*?)\\];", RegexOptions.Singleline)]
    private static partial Regex RouteTable();

    [GeneratedRegex("const factories = \\{(?<body>[^}]*)\\}")]
    private static partial Regex ViewFactories();

    [GeneratedRegex("id: '(?<id>[a-z]+)'")]
    private static partial Regex ItemId();

    [GeneratedRegex("(?<key>[a-z]+): [A-Za-z]+View")]
    private static partial Regex ViewKey();
}
