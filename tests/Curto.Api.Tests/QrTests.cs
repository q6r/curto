using System.Net;
using System.Net.Http.Json;
using System.Xml.Linq;
using Curto.Api.Tests.Support;

namespace Curto.Api.Tests;

public sealed class BaseUrlFactory : CurtoFactory
{
    protected override void Configure(IDictionary<string, string?> settings) => settings["Curto:BaseUrl"] = "https://s.example.com";
}

public sealed class QrFactory : CurtoFactory
{
    public override async Task InitializeAsync()
    {
        using var client = CreateApiClient();
        await CreateLinkAsync(client, "https://example.com/qr", body => body["slug"] = "qr-target");
        await CreateLinkAsync(client, "https://example.com/qr2", body => body["slug"] = "qr-other");
    }
}

public sealed class QrTests : IClassFixture<QrFactory>, IDisposable
{
    private readonly HttpClient _client;

    public QrTests(QrFactory factory) => _client = factory.CreateApiClient(withKey: false);

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task Returns_a_valid_svg_document_for_a_link()
    {
        using var response = await _client.GetAsync("/qr/qr-target.svg");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/svg+xml", response.Content.Headers.ContentType?.MediaType);
        var svg = await response.Content.ReadAsStringAsync();
        var document = XDocument.Parse(svg); // throws unless well-formed
        Assert.Equal("svg", document.Root!.Name.LocalName);
        Assert.Equal("http://www.w3.org/2000/svg", document.Root.Name.NamespaceName);
        Assert.Equal("256", document.Root.Attribute("width")!.Value);
        Assert.Equal("256", document.Root.Attribute("height")!.Value);
        Assert.Contains(document.Descendants(), e => e.Name.LocalName is "path" or "rect");
    }

    [Theory]
    [InlineData("64")]
    [InlineData("128")]
    [InlineData("512")]
    [InlineData("2048")]
    public async Task The_size_parameter_sets_the_pixel_size(string size)
    {
        using var response = await _client.GetAsync($"/qr/qr-target.svg?size={size}");

        var root = XDocument.Parse(await response.Content.ReadAsStringAsync()).Root!;
        Assert.Equal(size, root.Attribute("width")!.Value);
        Assert.Equal(size, root.Attribute("height")!.Value);
    }

    [Theory]
    [InlineData("1a2b3c", "ffffff", "#1a2b3c", "#ffffff")]
    [InlineData("%231A2B3C", "%23FFF", "#1a2b3c", "#fff")]
    [InlineData("abc", "012345", "#abc", "#012345")]
    public async Task Colours_are_applied_as_validated_hex_values(string dark, string light, string expectedDark, string expectedLight)
    {
        using var response = await _client.GetAsync($"/qr/qr-target.svg?dark={dark}&light={light}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var svg = await response.Content.ReadAsStringAsync();
        Assert.Contains(expectedDark, svg, StringComparison.Ordinal);
        Assert.Contains(expectedLight, svg, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("size=63")]
    [InlineData("size=2049")]
    [InlineData("size=0")]
    [InlineData("size=-128")]
    [InlineData("size=abc")]
    [InlineData("size=1e3")]
    [InlineData("size=128.5")]
    [InlineData("size=%2B128")]
    [InlineData("size=")]
    [InlineData("dark=red")]
    [InlineData("dark=12345")]
    [InlineData("dark=gggggg")]
    [InlineData("dark=%23%23fff")]
    [InlineData("dark=fff%22%20onload%3D%22alert(1)")]
    [InlineData("light=%3Cscript%3E")]
    [InlineData("light=rgb(0,0,0)")]
    [InlineData("dark=")]
    public async Task Invalid_parameters_are_rejected_with_a_field_error(string query)
    {
        using var response = await _client.GetAsync($"/qr/qr-target.svg?{query}");

        var problem = await response.ExpectProblemAsync(400);
        var field = query[..query.IndexOf('=', StringComparison.Ordinal)];
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), problem.ToString());
    }

    [Fact]
    public async Task Several_bad_parameters_are_reported_together()
    {
        using var response = await _client.GetAsync("/qr/qr-target.svg?size=1&dark=x&light=y");

        var problem = await response.ExpectProblemAsync(400);
        Assert.Equal(["size", "dark", "light"], problem.GetProperty("errors").Names());
    }

    [Fact]
    public async Task Unknown_slugs_are_a_404_problem()
    {
        using var response = await _client.GetAsync("/qr/does-not-exist.svg");

        await response.ExpectProblemAsync(404);
    }

    [Fact]
    public async Task Output_is_deterministic_and_depends_on_the_link()
    {
        var first = await (await _client.GetAsync("/qr/qr-target.svg")).Content.ReadAsStringAsync();
        var again = await (await _client.GetAsync("/qr/qr-target.svg")).Content.ReadAsStringAsync();
        var other = await (await _client.GetAsync("/qr/qr-other.svg")).Content.ReadAsStringAsync();

        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
    }

    [Fact]
    public async Task Without_a_configured_base_url_the_response_is_not_cacheable_because_it_depends_on_the_host_header()
    {
        using var response = await _client.GetAsync("/qr/qr-target.svg");

        Assert.True(response.Headers.CacheControl!.NoStore);
    }

    [Fact]
    public async Task The_extension_is_part_of_the_route()
    {
        using var noExtension = await _client.GetAsync("/qr/qr-target");
        using var wrongExtension = await _client.GetAsync("/qr/qr-target.png");

        Assert.Equal(HttpStatusCode.NotFound, noExtension.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, wrongExtension.StatusCode);
    }
}

public sealed class QrWithBaseUrlTests : IClassFixture<BaseUrlFactory>, IDisposable
{
    private readonly HttpClient _client;

    public QrWithBaseUrlTests(BaseUrlFactory factory) => _client = factory.CreateApiClient();

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task A_configured_base_url_makes_urls_stable_and_the_qr_cacheable()
    {
        var link = await CurtoFactory.CreateLinkAsync(_client, "https://example.com/based", body => body["slug"] = "based-link");

        using var response = await _client.GetAsync("/qr/based-link.svg");

        Assert.Equal("https://s.example.com/based-link", link.Str("shortUrl"));
        Assert.Equal("public, max-age=3600", response.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task The_host_header_cannot_change_the_short_url_when_a_base_url_is_configured()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/links")
        {
            Content = JsonContent.Create(new { url = "https://example.com/hostile", slug = "host-header" }),
        };
        request.Headers.Host = "evil.example";

        using var response = await _client.SendAsync(request);

        var body = await response.JsonAsync();
        Assert.Equal("https://s.example.com/host-header", body.Str("shortUrl"));
    }
}
