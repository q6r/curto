using System.Net;
using System.Net.Http.Json;
using Curto.Api.Tests.Support;

namespace Curto.Api.Tests;

public sealed class PrivateTargetsFactory : CurtoFactory
{
    protected override void Configure(IDictionary<string, string?> settings) => settings["Curto:BlockPrivateTargets"] = "true";
}

public sealed class UrlValidationApiTests : IClassFixture<CurtoFactory>, IDisposable
{
    private readonly HttpClient _client;

    public UrlValidationApiTests(CurtoFactory factory) => _client = factory.CreateApiClient();

    public void Dispose() => _client.Dispose();

    public static TheoryData<string> DangerousOrMalformedUrls => new()
    {
        "javascript:alert(document.cookie)",
        "JaVaScRiPt:alert(1)",
        "data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==",
        "file:///C:/Windows/win.ini",
        "file://localhost/etc/passwd",
        "ftp://example.com/file.txt",
        "mailto:someone@example.com",
        "vbscript:msgbox(1)",
        "blob:https://example.com/550e8400-e29b-41d4-a716-446655440000",
        "https://user:password@example.com/",
        "https://user@example.com/",
        "https://example.com@evil.example/",
        "//example.com/path",
        "/just/a/path",
        "example.com",
        "http://",
        "https://",
        "http:///missing-host",
        "https://exa mple.com/",
        "   ",
        "not a url",
        "https://example.com/\r\nInjected: header",
    };

    [Theory]
    [MemberData(nameof(DangerousOrMalformedUrls))]
    public async Task Dangerous_or_malformed_targets_are_rejected_with_a_field_error(string url)
    {
        using var response = await _client.PostAsJsonAsync("/api/links", new { url });

        var problem = await response.ExpectProblemAsync(400);
        var messages = problem.GetProperty("errors").GetProperty("url").EnumerateArray().ToArray();
        Assert.NotEmpty(messages);
    }

    [Fact]
    public async Task A_missing_or_null_url_is_rejected()
    {
        using var missing = await _client.SendJsonAsync(HttpMethod.Post, "/api/links", "{}");
        using var nullUrl = await _client.SendJsonAsync(HttpMethod.Post, "/api/links", """{"url":null}""");
        using var number = await _client.SendJsonAsync(HttpMethod.Post, "/api/links", """{"url":42}""");

        await missing.ExpectProblemAsync(400);
        await nullUrl.ExpectProblemAsync(400);
        await number.ExpectProblemAsync(400);
    }

    [Fact]
    public async Task Urls_longer_than_2048_characters_are_rejected_and_exactly_2048_is_accepted()
    {
        const string prefix = "https://example.com/";
        var exactly = prefix + new string('a', 2048 - prefix.Length);

        using var accepted = await _client.PostAsJsonAsync("/api/links", new { url = exactly });
        using var rejected = await _client.PostAsJsonAsync("/api/links", new { url = exactly + "a" });

        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        var problem = await rejected.ExpectProblemAsync(400);
        Assert.Contains("2048", problem.GetProperty("errors").GetProperty("url")[0].GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://example.com", "https://example.com/")]
    [InlineData("HTTPS://Example.COM/Path?Q=1", "https://example.com/Path?Q=1")]
    [InlineData("https://example.com/caf\u00e9?q=\u00fc", "https://example.com/caf%C3%A9?q=%C3%BC")]
    [InlineData("http://localhost:3000/dev", "http://localhost:3000/dev")]
    [InlineData("http://192.168.1.10:8080/", "http://192.168.1.10:8080/")]
    public async Task Valid_urls_are_stored_in_normal_form_and_redirect_to_it(string input, string normalised)
    {
        var link = await CurtoFactory.CreateLinkAsync(_client, input);

        Assert.Equal(normalised, link.Str("url"));
        using var redirect = await _client.GetAsync("/" + link.Str("slug"));
        Assert.Equal(HttpStatusCode.Found, redirect.StatusCode);
        Assert.Equal(normalised, redirect.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Private_targets_are_allowed_unless_the_deployment_blocks_them()
    {
        using var response = await _client.PostAsJsonAsync("/api/links", new { url = "http://127.0.0.1:8080/admin" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }
}

public sealed class PrivateTargetBlockingTests : IClassFixture<PrivateTargetsFactory>, IDisposable
{
    private readonly HttpClient _client;

    public PrivateTargetBlockingTests(PrivateTargetsFactory factory) => _client = factory.CreateApiClient();

    public void Dispose() => _client.Dispose();

    [Theory]
    [InlineData("http://localhost/")]
    [InlineData("http://localhost:8080/admin")]
    [InlineData("http://sub.localhost/")]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://127.1/")]
    [InlineData("http://2130706433/")]
    [InlineData("http://0x7f000001/")]
    [InlineData("http://10.1.2.3/")]
    [InlineData("http://172.16.5.4/")]
    [InlineData("http://192.168.0.1/")]
    [InlineData("http://169.254.169.254/latest/meta-data/iam")]
    [InlineData("http://[::1]/")]
    [InlineData("http://[::ffff:10.0.0.1]/")]
    [InlineData("http://[fd00::1]/")]
    [InlineData("http://intranet/wiki")]
    [InlineData("http://printer.local/")]
    [InlineData("http://0.0.0.0/")]
    public async Task Private_loopback_and_link_local_targets_are_rejected(string url)
    {
        using var response = await _client.PostAsJsonAsync("/api/links", new { url });

        var problem = await response.ExpectProblemAsync(400);
        Assert.Contains("private", problem.GetProperty("errors").GetProperty("url")[0].GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://example.com/")]
    [InlineData("https://sub.example.org/path?x=1")]
    [InlineData("http://8.8.8.8/")]
    [InlineData("http://[2606:4700:4700::1111]/")]
    public async Task Public_targets_are_still_accepted(string url)
    {
        using var response = await _client.PostAsJsonAsync("/api/links", new { url });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task The_policy_also_applies_when_a_link_is_patched()
    {
        var link = await CurtoFactory.CreateLinkAsync(_client, "https://example.com/ok", body => body["slug"] = "patch-private");

        using var response = await _client.SendJsonAsync(HttpMethod.Patch, "/api/links/patch-private", """{"url":"http://10.0.0.1/"}""");

        await response.ExpectProblemAsync(400);
        using var unchanged = await _client.GetAsync("/api/links/patch-private");
        Assert.Equal("https://example.com/ok", (await unchanged.JsonAsync()).Str("url"));
        Assert.Equal("patch-private", link.Str("slug"));
    }
}
