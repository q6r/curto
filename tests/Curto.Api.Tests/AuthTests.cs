using System.Net;
using System.Net.Http.Json;
using Curto.Api.Services;
using Curto.Api.Tests.Support;
using Curto.Api.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Curto.Api.Tests;

public sealed class NoConfiguredKeyFactory : CurtoFactory
{
    protected override bool ConfigureApiKey => false;
}

public sealed class ProductionWithoutKeyFactory : CurtoFactory
{
    protected override string EnvironmentName => "Production";

    protected override bool ConfigureApiKey => false;
}

public sealed class ProductionWithKeyFactory : CurtoFactory
{
    protected override string EnvironmentName => "Production";
}

public sealed class AuthTests : IClassFixture<CurtoFactory>, IDisposable
{
    private readonly CurtoFactory _factory;
    private readonly HttpClient _anonymous;
    private readonly HttpClient _authenticated;

    public AuthTests(CurtoFactory factory)
    {
        _factory = factory;
        _anonymous = factory.CreateApiClient(withKey: false);
        _authenticated = factory.CreateApiClient();
    }

    public void Dispose()
    {
        _anonymous.Dispose();
        _authenticated.Dispose();
    }

    public static TheoryData<string, string> ManagementEndpoints => new()
    {
        { "GET", "/api/links" },
        { "POST", "/api/links" },
        { "GET", "/api/links/some-link" },
        { "PATCH", "/api/links/some-link" },
        { "DELETE", "/api/links/some-link" },
        { "GET", "/api/links/some-link/stats" },
        { "GET", "/api/stats/overview" },
        { "GET", "/api/events" },
    };

    [Theory]
    [MemberData(nameof(ManagementEndpoints))]
    public async Task Management_endpoints_require_the_key(string method, string path)
    {
        // The body is deliberately malformed: authentication must be decided before anything is parsed.
        using var request = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = method is "POST" or "PATCH" ? new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json") : null,
        };

        using var response = await _anonymous.SendAsync(request);

        var problem = await response.ExpectProblemAsync(401);
        Assert.Equal("ApiKey realm=\"curto\"", response.Headers.WwwAuthenticate.ToString());
        Assert.Contains("X-API-Key", problem.Str("detail"), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(ManagementEndpoints))]
    public async Task A_wrong_key_is_rejected_everywhere(string method, string path)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add("X-API-Key", CurtoFactory.ApiKey + "x");

        using var response = await _anonymous.SendAsync(request);

        await response.ExpectProblemAsync(401);
    }

    [Fact]
    public async Task The_correct_key_is_accepted_whatever_the_header_name_case()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/links");
        request.Headers.Add("x-api-key", CurtoFactory.ApiKey);

        using var response = await _anonymous.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("test-api-key")]
    [InlineData("TEST-API-KEY-0123456789ABCDEF")]
    [InlineData("test-api-key-0123456789abcde")]
    public async Task Near_misses_are_rejected(string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/links");
        request.Headers.TryAddWithoutValidation("X-API-Key", key);

        using var response = await _anonymous.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_key_is_never_accepted_in_the_url()
    {
        foreach (var path in new[]
        {
            $"/api/links?apiKey={CurtoFactory.ApiKey}",
            $"/api/links?api_key={CurtoFactory.ApiKey}",
            $"/api/links?X-API-Key={CurtoFactory.ApiKey}",
            $"/api/events?apiKey={CurtoFactory.ApiKey}",
        })
        {
            using var response = await _anonymous.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task Other_credential_schemes_do_not_work()
    {
        using var bearer = new HttpRequestMessage(HttpMethod.Get, "/api/links");
        bearer.Headers.TryAddWithoutValidation("Authorization", "Bearer " + CurtoFactory.ApiKey);
        using var basic = new HttpRequestMessage(HttpMethod.Get, "/api/links");
        basic.Headers.TryAddWithoutValidation("Authorization", "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("x:" + CurtoFactory.ApiKey)));

        using var first = await _anonymous.SendAsync(bearer);
        using var second = await _anonymous.SendAsync(basic);

        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
    }

    [Fact]
    public async Task Repeating_the_header_is_rejected_even_if_one_value_is_right()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/links");
        request.Headers.Add("X-API-Key", [CurtoFactory.ApiKey, "another"]);

        using var response = await _anonymous.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Public_endpoints_need_no_key()
    {
        await CurtoFactory.CreateLinkAsync(_authenticated, "https://example.com/public", body => body["slug"] = "public-one");

        foreach (var path in new[] { "/healthz", "/readyz", "/openapi/v1.json", "/docs/", "/qr/public-one.svg", "/", "/index.html" })
        {
            using var response = await _anonymous.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using var redirect = await _anonymous.GetAsync("/public-one");
        Assert.Equal(HttpStatusCode.Found, redirect.StatusCode);
    }

    [Fact]
    public async Task Failed_authentication_creates_nothing()
    {
        using var response = await _anonymous.PostAsJsonAsync("/api/links", new { url = "https://example.com/never", slug = "never-created" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var check = await _authenticated.GetAsync("/api/links/never-created");
        Assert.Equal(HttpStatusCode.NotFound, check.StatusCode);
        Assert.NotNull(_factory);
    }
}

public sealed class KeyProvisioningTests
{
    [Fact]
    public async Task Development_generates_a_key_when_none_is_configured()
    {
        await using var factory = new NoConfiguredKeyFactory();
        using var anonymous = factory.CreateClient();
        var verifier = factory.Services.GetRequiredService<ApiKeyVerifier>();

        var generated = verifier.GeneratedKey;

        Assert.NotNull(generated);
        Assert.StartsWith("dev_", generated, StringComparison.Ordinal);
        Assert.True(generated.Length >= 32, generated);
        using var denied = await anonymous.GetAsync("/api/links");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/links");
        request.Headers.Add("X-API-Key", generated);
        using var allowed = await anonymous.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task Each_run_generates_a_different_key()
    {
        await using var first = new NoConfiguredKeyFactory();
        await using var second = new NoConfiguredKeyFactory();

        var a = first.Services.GetRequiredService<ApiKeyVerifier>().GeneratedKey;
        var b = second.Services.GetRequiredService<ApiKeyVerifier>().GeneratedKey;

        Assert.NotEqual(a, b);
    }

    [Fact]
    public async Task A_configured_key_is_used_as_is_and_nothing_is_generated()
    {
        await using var factory = new CurtoFactory();
        using var client = factory.CreateClient();

        Assert.Null(factory.Services.GetRequiredService<ApiKeyVerifier>().GeneratedKey);
    }

    [Fact]
    public async Task Production_refuses_to_start_without_a_key()
    {
        await using var factory = new ProductionWithoutKeyFactory();

        var exception = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(exception);
        Assert.Contains("CURTO_API_KEY", Describe(exception), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Production_starts_normally_with_a_key()
    {
        await using var factory = new ProductionWithKeyFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(factory.Services.GetRequiredService<ApiKeyVerifier>().GeneratedKey);
    }

    private static string Describe(Exception exception)
    {
        List<string> parts = [];
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            parts.Add(current.Message);
        }

        return string.Join(" | ", parts);
    }
}
