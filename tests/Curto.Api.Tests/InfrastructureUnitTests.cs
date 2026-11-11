using System.Net;
using System.Text.Json;
using Curto.Api.Infrastructure;
using Curto.Api.Services;
using Curto.Api.Storage;
using Curto.Api.Tests.Support;
using Microsoft.Extensions.Options;

namespace Curto.Api.Tests;

public sealed class InfrastructureUnitTests
{
    private sealed class StubHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond());
    }

    private static Func<string, string?> Env(params (string Name, string Value)[] values)
    {
        var map = values.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);
        return name => map.GetValueOrDefault(name);
    }

    // ----- Docker health probe -----

    [Fact]
    public void Probe_defaults_to_the_container_port()
    {
        Assert.Equal("http://127.0.0.1:8080/healthz", HealthProbe.ResolveUrl(Env()).ToString());
    }

    [Theory]
    [InlineData("http://+:5000", "5000")]
    [InlineData("http://*:7000;https://*:7443", "7000")]
    [InlineData("https://+:7443;http://0.0.0.0:7001", "7001")]
    [InlineData("http://127.0.0.1:17100/", "17100")]
    public void Probe_reads_the_port_from_ASPNETCORE_URLS(string urls, string port)
    {
        Assert.Equal($"http://127.0.0.1:{port}/healthz", HealthProbe.ResolveUrl(Env(("ASPNETCORE_URLS", urls))).ToString());
    }

    [Fact]
    public void Probe_reads_the_port_from_ASPNETCORE_HTTP_PORTS()
    {
        var url = HealthProbe.ResolveUrl(Env(("ASPNETCORE_HTTP_PORTS", "9090;9091")));

        Assert.Equal("http://127.0.0.1:9090/healthz", url.ToString());
    }

    [Fact]
    public void Probe_ignores_https_only_urls()
    {
        var url = HealthProbe.ResolveUrl(Env(("ASPNETCORE_URLS", "https://+:443"), ("ASPNETCORE_HTTP_PORTS", "8081")));

        Assert.Equal("http://127.0.0.1:8081/healthz", url.ToString());
    }

    [Fact]
    public async Task Probe_exits_zero_on_success_and_one_otherwise()
    {
        var url = new Uri("http://127.0.0.1:1/healthz");

        using var ok = new HttpClient(new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK)));
        using var unavailable = new HttpClient(new StubHandler(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using var broken = new HttpClient(new StubHandler(() => throw new HttpRequestException("refused")));
        using var slow = new HttpClient(new StubHandler(() => throw new TaskCanceledException("timeout")));

        Assert.Equal(0, await HealthProbe.RunAsync(url, ok, CancellationToken.None));
        Assert.Equal(1, await HealthProbe.RunAsync(url, unavailable, CancellationToken.None));
        Assert.Equal(1, await HealthProbe.RunAsync(url, broken, CancellationToken.None));
        Assert.Equal(1, await HealthProbe.RunAsync(url, slow, CancellationToken.None));
    }

    // ----- Retention schedule -----

    [Theory]
    [InlineData("2026-03-15T02:00:00Z", "01:00:00")]
    [InlineData("2026-03-15T03:00:00Z", "1.00:00:00")]
    [InlineData("2026-03-15T03:00:01Z", "23:59:59")]
    [InlineData("2026-03-15T23:59:00Z", "03:01:00")]
    [InlineData("2026-03-15T00:00:00Z", "03:00:00")]
    public void Nightly_cleanup_runs_at_0300_utc(string now, string expectedWait)
    {
        var wait = RetentionService.UntilNextRun(DateTimeOffset.Parse(now, System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(TimeSpan.Parse(expectedWait, System.Globalization.CultureInfo.InvariantCulture), wait);
    }

    [Fact]
    public void Nightly_schedule_uses_utc_even_for_offset_instants()
    {
        var localNoon = new DateTimeOffset(2026, 3, 15, 12, 0, 0, TimeSpan.FromHours(-3)); // 15:00 UTC

        Assert.Equal(TimeSpan.FromHours(12), RetentionService.UntilNextRun(localNoon));
    }

    // ----- API key -----

    [Fact]
    public void Api_key_comparison_accepts_only_the_exact_key()
    {
        var verifier = new ApiKeyVerifier(
            Options.Create(new CurtoOptions { ApiKey = "correct-horse-battery-staple" }),
            new FakeHostEnvironment("Production"));

        Assert.True(verifier.IsValid("correct-horse-battery-staple"));
        Assert.False(verifier.IsValid("correct-horse-battery-staplE"));
        Assert.False(verifier.IsValid("correct-horse-battery-stapl"));
        Assert.False(verifier.IsValid("correct-horse-battery-staple "));
        Assert.False(verifier.IsValid("x"));
        Assert.False(verifier.IsValid(string.Empty));
        Assert.False(verifier.IsValid(null));
        Assert.Null(verifier.GeneratedKey);
    }

    // ----- Correlation ids -----

    [Theory]
    [InlineData("abc-123_DEF.456", true)]
    [InlineData("a", true)]
    [InlineData("0123456789012345678901234567890123456789012345678901234567890123", true)]  // 64
    [InlineData("01234567890123456789012345678901234567890123456789012345678901234", false)] // 65
    [InlineData("", false)]
    [InlineData("has space", false)]
    [InlineData("semi;colon", false)]
    [InlineData("<script>", false)]
    [InlineData("new\nline", false)]
    [InlineData("café", false)]
    public void Correlation_ids_are_short_and_harmless(string candidate, bool acceptable)
    {
        Assert.Equal(acceptable, CorrelationIdMiddleware.IsAcceptable(candidate));
    }

    // ----- Client address handling -----

    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("2001:db8:1:2:3:4:5:6", "20010DB800010002/64")]
    [InlineData("2001:db8:1:2:ffff:ffff:ffff:ffff", "20010DB800010002/64")]
    public void Rate_limit_key_groups_ipv6_by_slash_64(string address, string expected)
    {
        Assert.Equal(expected, ClientAddress.RateLimitKey(IPAddress.Parse(address)));
    }

    [Fact]
    public void Unknown_address_has_a_shared_key()
    {
        Assert.Equal("unknown", ClientAddress.RateLimitKey(null));
    }

    // ----- LIKE escaping -----

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("100%", "100\\%")]
    [InlineData("a_b", "a\\_b")]
    [InlineData("back\\slash", "back\\\\slash")]
    [InlineData("%_\\", "\\%\\_\\\\")]
    public void Like_wildcards_are_escaped(string input, string expected)
    {
        Assert.Equal(expected, LinkRepository.EscapeLike(input));
    }

    // ----- Timestamps -----

    private static readonly JsonSerializerOptions TimestampJson = new() { Converters = { new UtcDateTimeOffsetConverter() } };

    [Fact]
    public void Timestamps_serialise_as_utc_with_z()
    {
        Assert.Equal("\"2026-03-15T10:30:00Z\"", JsonSerializer.Serialize(new DateTimeOffset(2026, 3, 15, 7, 30, 0, TimeSpan.FromHours(-3)), TimestampJson));
        Assert.Equal("\"2026-03-15T10:30:00.5Z\"", JsonSerializer.Serialize(new DateTimeOffset(2026, 3, 15, 10, 30, 0, 500, TimeSpan.Zero), TimestampJson));
    }

    [Theory]
    [InlineData("\"2026-03-15T10:30:00Z\"", "2026-03-15T10:30:00Z")]
    [InlineData("\"2026-03-15T13:30:00+03:00\"", "2026-03-15T10:30:00Z")]
    [InlineData("\"2026-03-15T07:30:00-03:00\"", "2026-03-15T10:30:00Z")]
    [InlineData("\"2026-03-15T10:30:00.250Z\"", "2026-03-15T10:30:00.25Z")]
    public void Timestamps_with_an_offset_are_read_as_utc(string json, string expectedUtc)
    {
        var value = JsonSerializer.Deserialize<DateTimeOffset>(json, TimestampJson);

        Assert.Equal(TimeSpan.Zero, value.Offset);
        Assert.Equal(DateTimeOffset.Parse(expectedUtc, System.Globalization.CultureInfo.InvariantCulture), value);
    }

    [Theory]
    [InlineData("\"2026-03-15T10:30:00z\"")]   // the ISO 8601 profile only allows an upper-case Z
    [InlineData("\"2026-03-15T10:30:00\"")]    // no offset: would depend on the server's time zone
    [InlineData("\"2026-03-15\"")]
    [InlineData("\"15/03/2026 10:30\"")]
    [InlineData("\"tomorrow\"")]
    [InlineData("\"\"")]
    [InlineData("1773570600")]
    [InlineData("true")]
    [InlineData("null")]
    public void Timestamps_without_an_explicit_offset_or_in_other_formats_are_rejected(string json)
    {
        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<DateTimeOffset>(json, TimestampJson));
    }
}
