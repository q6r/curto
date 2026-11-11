using Curto.Api.Infrastructure;
using Curto.Api.Services;
using Microsoft.Extensions.Options;

namespace Curto.Api.Tests;

public sealed class TargetUrlValidatorTests
{
    private static TargetUrlValidator Create(bool blockPrivate = false) =>
        new(Options.Create(new CurtoOptions { BlockPrivateTargets = blockPrivate }));

    [Theory]
    [InlineData("https://example.com", "https://example.com/")]
    [InlineData("  https://example.com/a?b=1#c  ", "https://example.com/a?b=1#c")]
    [InlineData("HTTPS://EXAMPLE.COM/Path", "https://example.com/Path")]
    [InlineData("http://example.com:8080/x", "http://example.com:8080/x")]
    [InlineData("https://example.com/a b?x=é#f", "https://example.com/a%20b?x=%C3%A9#f")]
    [InlineData("https://example.com/café", "https://example.com/caf%C3%A9")]
    [InlineData("https://example.com/%7Euser", "https://example.com/~user")]
    [InlineData("http://localhost:3000/", "http://localhost:3000/")]
    [InlineData("http://192.168.1.10/admin", "http://192.168.1.10/admin")]
    public void Accepts_and_normalises_web_urls(string input, string expected)
    {
        var check = Create().Check(input);

        Assert.True(check.IsValid, check.Error);
        Assert.Equal(expected, check.Url);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("javascript:alert(1)")]
    [InlineData("JAVASCRIPT:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/file")]
    [InlineData("mailto:someone@example.com")]
    [InlineData("tel:+5511999999999")]
    [InlineData("ssh://example.com")]
    [InlineData("ws://example.com/socket")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("//example.com/path")]
    [InlineData("/relative/path")]
    [InlineData("example.com")]
    [InlineData("www.example.com/path")]
    [InlineData("http:example.com")]
    [InlineData("http:/example.com")]
    [InlineData("http:\\\\example.com")]
    [InlineData("http://")]
    [InlineData("https:///path")]
    [InlineData("https://exa mple.com/")]
    [InlineData("https://example.com/\r\nSet-Cookie: a=b")]
    [InlineData("https://example.com/\u0000")]
    [InlineData("https://example.com/a\tb")]
    [InlineData("https://example.com/\u007F")]
    [InlineData("https://user@example.com/")]
    [InlineData("https://user:password@example.com/")]
    [InlineData("https://example.com@evil.example/")]
    [InlineData("https://:@example.com/")]
    public void Rejects_everything_that_is_not_an_absolute_http_url_without_credentials(string? input)
    {
        var check = Create().Check(input);

        Assert.False(check.IsValid, input);
        Assert.NotNull(check.Error);
    }

    // Eight-digit escapes on purpose: they keep invisible characters (line separator, no-break space) readable in the source.
    [Theory]
    [InlineData("https://example.com/target?a=1&b=%C3%A9", "https://example.com/target?a=1&b=%C3%A9")]
    [InlineData("https://example.com/caf%C3%A9/%E2%82%AC", "https://example.com/caf%C3%A9/%E2%82%AC")]
    [InlineData("https://example.com/a#frägment", "https://example.com/a#fr%C3%A4gment")]
    [InlineData("https://example.com/\U0001F600", "https://example.com/%F0%9F%98%80")]
    [InlineData("https://example.com/a%0D%0Ab", "https://example.com/a%0D%0Ab")]
    [InlineData("https://example.com/\U00002028\U000000A0x", "https://example.com/%E2%80%A8%C2%A0x")]
    [InlineData("https://bücher.example/ü", "https://xn--bcher-kva.example/%C3%BC")]
    [InlineData("https://BÜCHER.example:8443/p?q=ü", "https://xn--bcher-kva.example:8443/p?q=%C3%BC")]
    [InlineData("https://中文.example/", "https://xn--fiq228c.example/")]
    [InlineData("https://xn--bcher-kva.example/x", "https://xn--bcher-kva.example/x")]
    public void The_normal_form_is_pure_ascii_and_keeps_encoded_control_characters_encoded(string input, string expected)
    {
        var check = Create().Check(input);

        Assert.True(check.IsValid, check.Error);
        Assert.Equal(expected, check.Url);
        Assert.All(check.Url!, c => Assert.InRange(c, ' ', '~'));
    }

    [Fact]
    public void Random_unicode_urls_always_normalise_to_a_header_safe_string()
    {
        // A Location header is rejected by the server unless it is printable ASCII: whatever a client sends must end up that way.
        var random = new Random(20260310);
        var pieces = new[] { "a", "z", "%C3%A9", "%", "%2", "é", "€", "\U0001F600", "\U0000D800", "?", "&", "=", "#", "/", " ", "..", "%00", "%0D%0A" };
        var hosts = new[] { "example.com", "bücher.example", "中文.example", "EXAMPLE.org:8080", "xn--bcher-kva.example", "[::1]", "127.0.0.1" };
        var validator = Create();
        var accepted = 0;

        for (var i = 0; i < 5_000; i++)
        {
            var tail = string.Concat(Enumerable.Range(0, random.Next(1, 12)).Select(_ => pieces[random.Next(pieces.Length)]));

            var check = validator.Check("https://" + hosts[random.Next(hosts.Length)] + "/" + tail);

            if (check.IsValid)
            {
                accepted++;
                Assert.All(check.Url!, c => Assert.InRange(c, ' ', '~'));
                Assert.True(Uri.TryCreate(check.Url, UriKind.Absolute, out _), check.Url);
            }
        }

        Assert.True(accepted > 1_000, $"only {accepted} inputs were accepted; the generator is too hostile to be a useful test");
    }

    [Theory]
    [InlineData("https://example.com/\n")]
    [InlineData("\r\nhttps://example.com/")]
    [InlineData("\thttps://example.com/\t")]
    public void Trims_surrounding_whitespace_such_as_a_pasted_trailing_newline(string input)
    {
        var check = Create().Check(input);

        Assert.True(check.IsValid, check.Error);
        Assert.Equal("https://example.com/", check.Url);
    }

    [Fact]
    public void Limits_the_length_to_2048_characters()
    {
        const string prefix = "https://example.com/";

        var exactly = prefix + new string('a', TargetUrlValidator.MaxLength - prefix.Length);
        var oneOver = exactly + "a";

        Assert.True(Create().Check(exactly).IsValid);
        var rejected = Create().Check(oneOver);
        Assert.False(rejected.IsValid);
        Assert.Contains("2048", rejected.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Limits_the_length_after_normalisation_too()
    {
        // 700 non-ASCII characters are short as typed but become 6 bytes each once percent-encoded.
        var input = "https://example.com/" + new string('é', 700);

        var check = Create().Check(input);

        Assert.False(check.IsValid);
        Assert.Contains("normalised", check.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://localhost/")]
    [InlineData("http://127.0.0.1:8080/")]
    [InlineData("http://10.0.0.5/")]
    [InlineData("http://192.168.0.1/")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://2130706433/")]
    [InlineData("http://intranet/")]
    [InlineData("http://printer.local/")]
    public void Blocks_private_targets_only_when_asked_to(string url)
    {
        Assert.True(Create(blockPrivate: false).Check(url).IsValid);

        var blocked = Create(blockPrivate: true).Check(url);

        Assert.False(blocked.IsValid);
        Assert.Contains("private", blocked.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Public_targets_pass_even_when_private_targets_are_blocked()
    {
        Assert.True(Create(blockPrivate: true).Check("https://example.com/").IsValid);
        Assert.True(Create(blockPrivate: true).Check("http://8.8.8.8/").IsValid);
    }
}
