using System.Net;
using System.Text.RegularExpressions;
using Curto.Api.Tests.Support;

namespace Curto.Api.Tests;

public sealed class HeadersFactory : CurtoFactory
{
    public override async Task InitializeAsync()
    {
        using var client = CreateApiClient();
        await CreateLinkAsync(client, "https://example.com/headers", body => body["slug"] = "headers-link");
    }
}

public sealed partial class SecurityHeadersTests : IClassFixture<HeadersFactory>, IDisposable
{
    private readonly HttpClient _client;

    public SecurityHeadersTests(HeadersFactory factory) => _client = factory.CreateApiClient();

    public void Dispose() => _client.Dispose();

    public static TheoryData<string> Endpoints => new()
    {
        "/healthz",
        "/readyz",
        "/",
        "/index.html",
        "/headers-link",
        "/qr/headers-link.svg",
        "/api/links",
        "/api/stats/overview",
        "/nothing-here",
        "/api/nothing-here",
        "/openapi/v1.json",
    };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Every_response_carries_the_hardening_headers(string path)
    {
        using var response = await _client.GetAsync(path);

        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("default-src 'self'", csp, StringComparison.Ordinal);
        Assert.Contains("img-src 'self' data:", csp, StringComparison.Ordinal);
        Assert.Contains("script-src 'self'", csp, StringComparison.Ordinal);
        Assert.Contains("style-src 'self'", csp, StringComparison.Ordinal);
        Assert.Contains("object-src 'none'", csp, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
        Assert.Contains("base-uri 'none'", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-inline", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-eval", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("http:", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("*", csp, StringComparison.Ordinal);

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("strict-origin-when-cross-origin", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Contains("camera=()", response.Headers.GetValues("Permissions-Policy").Single(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/api/links")]
    [InlineData("/api/links/headers-link")]
    [InlineData("/api/stats/overview")]
    [InlineData("/api/nothing-here")]
    public async Task Management_responses_are_never_cached(string path)
    {
        using var response = await _client.GetAsync(path);

        Assert.True(response.Headers.CacheControl!.NoStore);
    }

    [Fact]
    public async Task The_redirect_and_its_errors_are_never_cached()
    {
        using var redirect = await _client.GetAsync("/headers-link");
        using var missing = await _client.GetAsync("/missing-link");

        Assert.True(redirect.Headers.CacheControl!.NoStore);
        Assert.True(missing.Headers.CacheControl!.NoStore);
    }

    [Fact]
    public async Task Strict_transport_security_is_sent_for_https_requests_only()
    {
        using var plain = await _client.GetAsync("/healthz");
        using var secure = await _client.GetAsync("https://localhost/healthz");

        Assert.False(plain.Headers.Contains("Strict-Transport-Security"));
        Assert.Equal("max-age=31536000", secure.Headers.GetValues("Strict-Transport-Security").Single());
    }

    [Fact]
    public async Task The_api_reference_gets_a_narrow_exception_with_a_fresh_nonce_every_time()
    {
        using var first = await _client.GetAsync("/docs/v1");
        using var second = await _client.GetAsync("/docs/v1");

        var a = first.Headers.GetValues("Content-Security-Policy").Single();
        var b = second.Headers.GetValues("Content-Security-Policy").Single();
        var nonceA = Nonce().Match(a).Groups[1].Value;
        var nonceB = Nonce().Match(b).Groups[1].Value;

        Assert.NotEqual(nonceA, nonceB);
        Assert.True(nonceA.Length >= 22, nonceA); // 128 bits of base64
        Assert.Contains("script-src 'self' 'nonce-", a, StringComparison.Ordinal);
        Assert.DoesNotContain("script-src 'self' 'unsafe-inline'", a, StringComparison.Ordinal);
        Assert.Contains("connect-src 'self'", a, StringComparison.Ordinal);
        Assert.Contains("object-src 'none'", a, StringComparison.Ordinal);

        // Nothing else gets the relaxed policy.
        using var other = await _client.GetAsync("/healthz");
        Assert.DoesNotContain("unsafe-inline", other.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/healthz")]
    [InlineData("/api/links")]
    [InlineData("/headers-link")]
    [InlineData("/qr/headers-link.svg")]
    [InlineData("/")]
    [InlineData("/nothing-here")]
    public async Task No_CORS_headers_are_ever_sent(string path)
    {
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Options, HttpMethod.Post })
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Add("Origin", "https://evil.example");
            request.Headers.Add("Access-Control-Request-Method", "POST");
            request.Headers.Add("Access-Control-Request-Headers", "x-api-key,content-type");

            using var response = await _client.SendAsync(request);

            var corsHeaders = response.Headers.Select(h => h.Key)
                .Concat(response.Content.Headers.Select(h => h.Key))
                .Where(name => name.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            Assert.Empty(corsHeaders);
            Assert.False(response.Headers.Contains("Vary") && response.Headers.GetValues("Vary").Any(v => v.Contains("Origin", StringComparison.OrdinalIgnoreCase)));
        }
    }

    [Fact]
    public async Task A_preflight_request_is_not_answered_as_if_cross_origin_calls_were_welcome()
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/links");
        request.Headers.Add("Origin", "https://evil.example");
        request.Headers.Add("Access-Control-Request-Method", "DELETE");

        using var response = await _client.SendAsync(request);

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task A_sane_correlation_id_is_echoed()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        request.Headers.Add("X-Correlation-Id", "trace-me.123_ABC");

        using var response = await _client.SendAsync(request);

        Assert.Equal("trace-me.123_ABC", response.Headers.GetValues("X-Correlation-Id").Single());
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("café")]
    [InlineData("0123456789012345678901234567890123456789012345678901234567890123456789")]
    public async Task A_hostile_correlation_id_is_replaced(string supplied)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", supplied);

        using var response = await _client.SendAsync(request);

        var id = response.Headers.GetValues("X-Correlation-Id").Single();
        Assert.NotEqual(supplied, id);
        Assert.Matches("^[0-9a-f]{32}$", id);
    }

    [Fact]
    public async Task Every_request_without_an_id_gets_a_distinct_one()
    {
        using var first = await _client.GetAsync("/healthz");
        using var second = await _client.GetAsync("/healthz");

        Assert.NotEqual(
            first.Headers.GetValues("X-Correlation-Id").Single(),
            second.Headers.GetValues("X-Correlation-Id").Single());
    }

    [GeneratedRegex("'nonce-([^']+)'")]
    private static partial Regex Nonce();
}
