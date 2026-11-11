using System.Net;
using System.Net.Http.Json;
using Curto.Api.Tests.Support;

namespace Curto.Api.Tests;

public sealed class TightLimitsFactory : CurtoFactory
{
    protected override void Configure(IDictionary<string, string?> settings)
    {
        settings["Curto:RateLimits:CreatePerMinute"] = "3";
        settings["Curto:RateLimits:RedirectPerMinute"] = "5";
        settings["Curto:RateLimits:ApiPerMinute"] = "4";
    }
}

public sealed class RateLimitTests : IClassFixture<TightLimitsFactory>, IDisposable
{
    private readonly HttpClient _client;

    public RateLimitTests(TightLimitsFactory factory) => _client = factory.CreateApiClient();

    public void Dispose() => _client.Dispose();

    private static HttpRequestMessage Create(string ip, string slug = "")
    {
        var body = new Dictionary<string, string> { ["url"] = "https://example.com/" };
        if (slug.Length > 0)
        {
            body["slug"] = slug;
        }

        return new HttpRequestMessage(HttpMethod.Post, "/api/links")
        {
            Headers = { { "X-Test-Ip", ip } },
            Content = JsonContent.Create(body),
        };
    }

    private static HttpRequestMessage Get(string path, string ip) => new(HttpMethod.Get, path) { Headers = { { "X-Test-Ip", ip } } };

    [Fact]
    public async Task Creating_links_is_limited_per_client_and_answers_429_with_a_problem()
    {
        for (var i = 0; i < 3; i++)
        {
            using var ok = await _client.SendAsync(Create("203.0.113.10"));
            Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        }

        using var limited = await _client.SendAsync(Create("203.0.113.10"));

        var problem = await limited.ExpectProblemAsync(429);
        Assert.Contains("Rate limit", problem.Str("detail"), StringComparison.Ordinal);
        var retryAfter = int.Parse(limited.Headers.GetValues("Retry-After").Single(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(retryAfter, 1, 60);
    }

    [Fact]
    public async Task The_create_budget_replaces_the_general_API_budget_for_that_endpoint()
    {
        const string ip = "203.0.113.11";
        for (var i = 0; i < 3; i++)
        {
            using var ok = await _client.SendAsync(Create(ip));
            Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        }

        using var rejected = await _client.SendAsync(Create(ip));
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);

        // The API budget is 4 and none of the four create attempts above has used any of it.
        for (var i = 0; i < 4; i++)
        {
            using var list = await _client.SendAsync(Get("/api/links?limit=1", ip));
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        }

        using var fifth = await _client.SendAsync(Get("/api/links?limit=1", ip));
        await fifth.ExpectProblemAsync(429);
    }

    [Fact]
    public async Task Redirects_have_their_own_budget_and_unknown_slugs_count_too()
    {
        const string ip = "203.0.113.12";
        for (var i = 0; i < 5; i++)
        {
            using var response = await _client.SendAsync(Get("/does-not-exist", ip));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        using var limited = await _client.SendAsync(Get("/does-not-exist", ip));
        await limited.ExpectProblemAsync(429);
    }

    [Fact]
    public async Task QR_codes_share_the_public_budget()
    {
        const string ip = "203.0.113.13";
        for (var i = 0; i < 5; i++)
        {
            using var response = await _client.SendAsync(Get("/qr/unknown-slug.svg", ip));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        using var limited = await _client.SendAsync(Get("/qr/unknown-slug.svg", ip));
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    [Fact]
    public async Task Budgets_are_per_client_address()
    {
        for (var i = 0; i < 3; i++)
        {
            using var response = await _client.SendAsync(Create("203.0.113.20"));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        using var blocked = await _client.SendAsync(Create("203.0.113.20"));
        using var otherClient = await _client.SendAsync(Create("203.0.113.21"));

        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.Equal(HttpStatusCode.Created, otherClient.StatusCode);
    }

    [Fact]
    public async Task Ipv6_clients_share_a_budget_per_slash_64()
    {
        using var a = await _client.SendAsync(Create("2001:db8:aaaa:1::1"));
        using var b = await _client.SendAsync(Create("2001:db8:aaaa:1::2"));
        using var c = await _client.SendAsync(Create("2001:db8:aaaa:1:ffff:ffff:ffff:ffff"));
        using var sameSubnet = await _client.SendAsync(Create("2001:db8:aaaa:1::3"));
        using var otherSubnet = await _client.SendAsync(Create("2001:db8:aaaa:2::1"));

        Assert.Equal(HttpStatusCode.Created, a.StatusCode);
        Assert.Equal(HttpStatusCode.Created, b.StatusCode);
        Assert.Equal(HttpStatusCode.Created, c.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, sameSubnet.StatusCode);
        Assert.Equal(HttpStatusCode.Created, otherSubnet.StatusCode);
    }

    [Fact]
    public async Task IPv4_mapped_ipv6_addresses_count_as_their_ipv4_address()
    {
        for (var i = 0; i < 3; i++)
        {
            using var response = await _client.SendAsync(Create("203.0.113.30"));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        using var mapped = await _client.SendAsync(Create("::ffff:203.0.113.30"));

        Assert.Equal(HttpStatusCode.TooManyRequests, mapped.StatusCode);
    }

    [Fact]
    public async Task Health_probes_are_never_limited()
    {
        for (var i = 0; i < 25; i++)
        {
            using var health = await _client.SendAsync(Get("/healthz", "203.0.113.40"));
            using var ready = await _client.SendAsync(Get("/readyz", "203.0.113.40"));
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        }
    }

    [Fact]
    public async Task Rejected_requests_do_not_create_links()
    {
        const string ip = "203.0.113.50";
        for (var i = 0; i < 3; i++)
        {
            using var response = await _client.SendAsync(Create(ip));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        using var rejected = await _client.SendAsync(Create(ip, "should-not-exist"));
        using var otherIp = await _client.SendAsync(Get("/api/links/should-not-exist", "203.0.113.51"));

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, otherIp.StatusCode);
    }
}
