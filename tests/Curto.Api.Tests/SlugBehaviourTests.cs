using System.Net;
using System.Net.Http.Json;
using Curto.Api.Services;
using Curto.Api.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Curto.Api.Tests;

/// <summary>Hands out a scripted sequence of slugs (repeating the last one) so collisions can be forced.</summary>
public sealed class ScriptedSlugGenerator : ISlugGenerator
{
    private Queue<string> _slugs = new(["scripted-default"]);

    /// <summary>How many slugs were requested since the last reset.</summary>
    public int Calls { get; private set; }

    /// <summary>Replaces the script and clears the call counter.</summary>
    /// <param name="slugs">Slugs to hand out in order; the last one repeats forever.</param>
    public void Reset(params string[] slugs)
    {
        _slugs = new Queue<string>(slugs);
        Calls = 0;
    }

    public string Next()
    {
        Calls++;
        return _slugs.Count > 1 ? _slugs.Dequeue() : _slugs.Peek();
    }
}

public sealed class SlugFactory : CurtoFactory
{
    public ScriptedSlugGenerator Generator { get; } = new();

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        services.RemoveAll<ISlugGenerator>();
        services.AddSingleton<ISlugGenerator>(Generator);
    }
}

public sealed class SlugBehaviourTests : IClassFixture<SlugFactory>, IDisposable
{
    private readonly SlugFactory _factory;
    private readonly HttpClient _client;

    public SlugBehaviourTests(SlugFactory factory)
    {
        _factory = factory;
        _client = factory.CreateApiClient();
    }

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task A_taken_custom_slug_is_a_409_with_a_machine_readable_code()
    {
        await CurtoFactory.CreateLinkAsync(_client, "https://example.com/one", body => body["slug"] = "taken-slug");

        using var response = await _client.PostAsJsonAsync("/api/links", new { url = "https://example.com/two", slug = "taken-slug" });

        var problem = await response.ExpectProblemAsync(409);
        Assert.Equal("slug_taken", problem.Str("code"));
        Assert.Contains("taken-slug", problem.Str("detail"), StringComparison.Ordinal);

        // The original link is untouched.
        using var original = await _client.GetAsync("/api/links/taken-slug");
        Assert.Equal("https://example.com/one", (await original.JsonAsync()).Str("url"));
    }

    [Fact]
    public async Task Slugs_differing_only_in_case_are_different_links()
    {
        await CurtoFactory.CreateLinkAsync(_client, "https://example.com/lower", body => body["slug"] = "case-test");
        await CurtoFactory.CreateLinkAsync(_client, "https://example.com/upper", body => body["slug"] = "Case-Test");

        using var lower = await _client.GetAsync("/case-test");
        using var upper = await _client.GetAsync("/Case-Test");

        Assert.Equal("https://example.com/lower", lower.Headers.Location!.ToString());
        Assert.Equal("https://example.com/upper", upper.Headers.Location!.ToString());
    }

    [Theory]
    [InlineData("api")]
    [InlineData("qr")]
    [InlineData("docs")]
    [InlineData("healthz")]
    [InlineData("readyz")]
    [InlineData("openapi")]
    [InlineData("assets")]
    [InlineData("admin")]
    [InlineData("DOCS")]
    [InlineData("Admin")]
    [InlineData("HealthZ")]
    public async Task Reserved_words_are_rejected_in_any_case(string slug)
    {
        using var response = await _client.PostAsJsonAsync("/api/links", new { url = "https://example.com", slug });

        // "qr" is also shorter than the minimum, so it fails the format rule first: still a 400 on the slug field.
        var problem = await response.ExpectProblemAsync(400);
        Assert.True(problem.GetProperty("errors").TryGetProperty("slug", out _));
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("has space")]
    [InlineData("dot.dot")]
    [InlineData("slash/slash")]
    [InlineData("café")]
    [InlineData("%2e%2e")]
    [InlineData("")]
    public async Task Malformed_custom_slugs_are_rejected(string slug)
    {
        using var response = await _client.PostAsJsonAsync("/api/links", new { url = "https://example.com", slug });

        var problem = await response.ExpectProblemAsync(400);
        Assert.True(problem.GetProperty("errors").TryGetProperty("slug", out _));
    }

    [Fact]
    public async Task A_slug_of_exactly_32_characters_is_accepted_and_33_is_not()
    {
        var ok = new string('a', 32);

        using var accepted = await _client.PostAsJsonAsync("/api/links", new { url = "https://example.com", slug = ok });
        using var rejected = await _client.PostAsJsonAsync("/api/links", new { url = "https://example.com", slug = ok + "a" });

        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        await rejected.ExpectProblemAsync(400);
    }

    [Fact]
    public async Task Collisions_on_generated_slugs_are_retried_with_a_fresh_slug()
    {
        await CurtoFactory.CreateLinkAsync(_client, "https://example.com/existing", body => body["slug"] = "collides-1");
        _factory.Generator.Reset("collides-1", "collides-1", "fresh-slug-1");

        var link = await CurtoFactory.CreateLinkAsync(_client, "https://example.com/new");

        Assert.Equal("fresh-slug-1", link.Str("slug"));
        Assert.Equal(3, _factory.Generator.Calls);
    }

    [Fact]
    public async Task Reserved_words_drawn_by_the_generator_are_skipped()
    {
        // "healthz" is exactly 7 base62 characters, so the real generator could in theory produce it.
        _factory.Generator.Reset("healthz", "Docs123", "never-reached");

        var link = await CurtoFactory.CreateLinkAsync(_client, "https://example.com/reserved-draw");

        Assert.Equal("Docs123", link.Str("slug"));
        Assert.Equal(2, _factory.Generator.Calls);
    }

    [Fact]
    public async Task Running_out_of_unique_slugs_is_a_503_not_a_crash()
    {
        await CurtoFactory.CreateLinkAsync(_client, "https://example.com/existing", body => body["slug"] = "always-taken");
        _factory.Generator.Reset("always-taken");

        using var response = await _client.PostAsJsonAsync("/api/links", new { url = "https://example.com/never" });

        var problem = await response.ExpectProblemAsync(503);
        Assert.Contains("Retry", problem.Str("detail"), StringComparison.Ordinal);
        Assert.True(response.Headers.Contains("Retry-After"));
        Assert.Equal(8, _factory.Generator.Calls);
    }
}
