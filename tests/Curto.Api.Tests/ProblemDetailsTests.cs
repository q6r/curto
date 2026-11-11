using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Curto.Api.Services;
using Curto.Api.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Curto.Api.Tests;

/// <summary>A slug source that fails with a message that must never reach a client.</summary>
internal sealed class ExplodingSlugGenerator : ISlugGenerator
{
    public const string Secret = "connection string = Server=internal;Password=hunter2";

    public string Next() => throw new InvalidOperationException(Secret);
}

public sealed class ExplodingFactory : CurtoFactory
{
    protected override void ConfigureTestServices(IServiceCollection services)
    {
        services.RemoveAll<ISlugGenerator>();
        services.AddSingleton<ISlugGenerator, ExplodingSlugGenerator>();
    }
}

public sealed class ProblemDetailsTests : IClassFixture<ExplodingFactory>, IDisposable
{
    private readonly HttpClient _client;

    public ProblemDetailsTests(ExplodingFactory factory) => _client = factory.CreateApiClient();

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task Unknown_routes_are_problems()
    {
        using var root = await _client.GetAsync("/a/b/c/d");
        using var api = await _client.GetAsync("/api/not-a-thing");

        var problem = await root.ExpectProblemAsync(404);
        Assert.Equal("/a/b/c/d", problem.Str("instance"));
        Assert.Equal("Not Found", problem.Str("title"));
        Assert.Equal("https://tools.ietf.org/html/rfc9110#section-15.5.5", problem.Str("type"));
        await api.ExpectProblemAsync(404);
    }

    [Fact]
    public async Task Wrong_methods_are_405_problems_with_an_allow_header()
    {
        using var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/stats/overview"));

        await response.ExpectProblemAsync(405);
        Assert.Contains("GET", response.Content.Headers.Allow);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("not json at all")]
    [InlineData("""{"url": "https://example.com", "slug": }""")]
    [InlineData("[]")]
    [InlineData("\"just a string\"")]
    [InlineData("""{"url": 12345}""")]
    [InlineData("""{"url": "https://example.com", "maxClicks": "five"}""")]
    [InlineData("""{"url": "https://example.com", "maxClicks": 1.5}""")]
    [InlineData("""{"url": "https://example.com", "tags": "not-an-array"}""")]
    [InlineData("""{"url": "https://example.com", "expiresAt": "tomorrow"}""")]
    [InlineData("""{"url": "https://example.com", "unknownMember": true}""")]
    public async Task Malformed_bodies_are_400_problems_without_internals(string body)
    {
        using var response = await _client.SendJsonAsync(HttpMethod.Post, "/api/links", body);

        var problem = await response.ExpectProblemAsync(400);
        var text = problem.ToString();
        Assert.DoesNotContain("System.Text.Json", text, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateLinkRequest", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_body_is_a_400_problem()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/links") { Content = new StringContent(string.Empty, Encoding.UTF8, "application/json") };

        using var response = await _client.SendAsync(request);

        await response.ExpectProblemAsync(400);
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/xml")]
    [InlineData("application/x-www-form-urlencoded")]
    public async Task A_non_json_content_type_is_a_415_problem(string mediaType)
    {
        using var response = await _client.SendJsonAsync(HttpMethod.Post, "/api/links", """{"url":"https://example.com"}""", mediaType);

        var problem = await response.ExpectProblemAsync(415);
        Assert.Contains("JSON", problem.Str("detail"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Oversized_bodies_are_rejected_before_they_are_read()
    {
        var huge = $$"""{"url":"https://example.com/","title":"{{new string('x', 20_000)}}"}""";

        using var response = await _client.SendJsonAsync(HttpMethod.Post, "/api/links", huge);

        var problem = await response.ExpectProblemAsync(413);
        Assert.Contains("too large", problem.Str("detail"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_body_just_under_the_limit_is_still_processed()
    {
        // 16,384 bytes in total: the title is capped at 200 characters, so this fails validation (400), not size (413).
        var body = $$"""{"url":"https://example.com/","title":"{{new string('x', 16_000)}}"}""";

        using var response = await _client.SendJsonAsync(HttpMethod.Post, "/api/links", body);

        var problem = await response.ExpectProblemAsync(400);
        Assert.True(problem.GetProperty("errors").TryGetProperty("title", out _));
    }

    [Fact]
    public async Task Unexpected_failures_are_500_problems_that_leak_nothing()
    {
        using var response = await _client.PostAsJsonAsync("/api/links", new { url = "https://example.com/boom" });

        var problem = await response.ExpectProblemAsync(500);
        var text = problem.ToString();
        Assert.Equal("Internal Server Error", problem.Str("title"));
        Assert.DoesNotContain("hunter2", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Password", text, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", text, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", text, StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(problem.Str("correlationId")), "a correlation id lets an operator find the log entry");
    }

    [Fact]
    public async Task Problems_are_json_even_when_the_client_asks_for_something_else()
    {
        foreach (var accept in new[] { "text/html", "text/plain", "application/xml", "image/png" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/missing-link");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));

            using var response = await _client.SendAsync(request);

            await response.ExpectProblemAsync(404);
        }
    }

    [Fact]
    public async Task Validation_problems_follow_the_standard_shape()
    {
        using var response = await _client.PostAsJsonAsync("/api/links", new { url = "javascript:alert(1)", slug = "x" });

        var problem = await response.ExpectProblemAsync(400);
        Assert.Equal("One or more validation errors occurred.", problem.Str("title"));
        Assert.Equal("https://tools.ietf.org/html/rfc9110#section-15.5.1", problem.Str("type"));
        Assert.Equal("/api/links", problem.Str("instance"));
        var errors = problem.GetProperty("errors");
        Assert.Equal(["url", "slug"], errors.Names());
        Assert.All(errors.EnumerateObject(), e => Assert.All(e.Value.EnumerateArray(), m => Assert.False(string.IsNullOrWhiteSpace(m.GetString()))));
    }

    [Fact]
    public async Task Every_problem_carries_the_same_set_of_members()
    {
        using var notFound = await _client.GetAsync("/nothing-here-at-all");
        using var unauthorized = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/links") { Headers = { { "X-API-Key", "wrong" } } });
        using var gone = await _client.GetAsync("/api/links/missing-xyz");

        foreach (var response in new[] { notFound, unauthorized, gone })
        {
            var problem = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            var names = problem.Names();
            foreach (var required in new[] { "type", "title", "status", "instance", "traceId", "correlationId" })
            {
                Assert.Contains(required, names);
            }
        }
    }
}
