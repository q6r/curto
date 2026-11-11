using Curto.Api.Services;

namespace Curto.Api.Tests;

public sealed class ReferrerParserTests
{
    [Theory]
    [InlineData("https://www.Example.com/path?q=1#frag", "example.com")]
    [InlineData("http://news.ycombinator.com/item?id=42", "news.ycombinator.com")]
    [InlineData("https://t.co/AbCdEf", "t.co")]
    [InlineData("https://user:secret@evil.example:8443/private?token=abc", "evil.example")]
    [InlineData("https://WWW.GOOGLE.COM/", "google.com")]
    [InlineData("https://sub.www.example.com/", "sub.www.example.com")]
    [InlineData("https://192.0.2.10/page", "192.0.2.10")]
    public void Keeps_only_the_host(string referer, string expected)
    {
        Assert.Equal(expected, ReferrerParser.HostOf(referer));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("/relative/path")]
    [InlineData("android-app://com.google.android.gm/")]
    [InlineData("ftp://files.example.com/")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,hello")]
    [InlineData("file:///etc/passwd")]
    public void Ignores_missing_or_non_web_referrers(string? referer)
    {
        Assert.Null(ReferrerParser.HostOf(referer));
    }

    [Fact]
    public void Ignores_absurdly_long_headers()
    {
        Assert.Null(ReferrerParser.HostOf("https://example.com/" + new string('a', 5000)));
    }
}
