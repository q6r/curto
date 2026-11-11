using Curto.Api.Services;

namespace Curto.Api.Tests;

public sealed class UserAgentParserTests
{
    [Theory]
    // Desktop browsers
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36", "Chrome", "Windows", "desktop")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36 Edg/130.0.2849.46", "Edge", "Windows", "desktop")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36 OPR/115.0.0.0", "Opera", "Windows", "desktop")]
    [InlineData("Mozilla/5.0 (X11; Linux x86_64; rv:132.0) Gecko/20100101 Firefox/132.0", "Firefox", "Linux", "desktop")]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.1 Safari/605.1.15", "Safari", "macOS", "desktop")]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 10.15; rv:132.0) Gecko/20100101 Firefox/132.0", "Firefox", "macOS", "desktop")]
    [InlineData("Mozilla/5.0 (X11; CrOS x86_64 14541.0.0) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36", "Chrome", "ChromeOS", "desktop")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; WOW64; Trident/7.0; rv:11.0) like Gecko", "Internet Explorer", "Windows", "desktop")]
    [InlineData("Mozilla/5.0 (compatible; MSIE 9.0; Windows NT 6.1; Trident/5.0)", "Internet Explorer", "Windows", "desktop")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/70.0.3538.102 Safari/537.36 Edge/18.19041", "Edge", "Windows", "desktop")]
    // Phones
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 17_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.1 Mobile/15E148 Safari/604.1", "Safari", "iOS", "mobile")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 17_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) CriOS/130.0.6723.37 Mobile/15E148 Safari/604.1", "Chrome", "iOS", "mobile")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 17_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) FxiOS/132.0 Mobile/15E148 Safari/605.1.15", "Firefox", "iOS", "mobile")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 17_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) EdgiOS/130.0.2849.52 Mobile/15E148 Safari/605.1.15", "Edge", "iOS", "mobile")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 17_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148", "Safari", "iOS", "mobile")]
    [InlineData("Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Mobile Safari/537.36", "Chrome", "Android", "mobile")]
    [InlineData("Mozilla/5.0 (Linux; Android 14; SAMSUNG SM-S918B) AppleWebKit/537.36 (KHTML, like Gecko) SamsungBrowser/25.0 Chrome/121.0.0.0 Mobile Safari/537.36", "Samsung Internet", "Android", "mobile")]
    [InlineData("Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Mobile Safari/537.36 EdgA/130.0.2849.48", "Edge", "Android", "mobile")]
    [InlineData("Mozilla/5.0 (Android 14; Mobile; rv:132.0) Gecko/132.0 Firefox/132.0", "Firefox", "Android", "mobile")]
    [InlineData("Opera/9.80 (Android; Opera Mini/36.2.2254/119.132; U; en) Presto/2.12.423 Version/12.16", "Opera", "Android", "mobile")]
    // Tablets
    [InlineData("Mozilla/5.0 (iPad; CPU OS 17_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.1 Mobile/15E148 Safari/604.1", "Safari", "iOS", "tablet")]
    [InlineData("Mozilla/5.0 (Linux; Android 13; SM-X700) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36", "Chrome", "Android", "tablet")]
    [InlineData("Mozilla/5.0 (Android 13; Tablet; rv:132.0) Gecko/132.0 Firefox/132.0", "Firefox", "Android", "tablet")]
    // Bots, preview fetchers and scripted clients
    [InlineData("Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)", "Bot", "Other", "bot")]
    [InlineData("Mozilla/5.0 (Linux; Android 6.0.1; Nexus 5X Build/MMB29P) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Mobile Safari/537.36 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)", "Bot", "Android", "bot")]
    [InlineData("Mozilla/5.0 (compatible; bingbot/2.0; +http://www.bing.com/bingbot.htm)", "Bot", "Other", "bot")]
    [InlineData("facebookexternalhit/1.1 (+http://www.facebook.com/externalhit_uatext.php)", "Bot", "Other", "bot")]
    [InlineData("Slackbot-LinkExpanding 1.0 (+https://api.slack.com/robots)", "Bot", "Other", "bot")]
    [InlineData("Twitterbot/1.0", "Bot", "Other", "bot")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) HeadlessChrome/130.0.0.0 Safari/537.36", "Bot", "Windows", "bot")]
    [InlineData("curl/8.4.0", "Bot", "Other", "bot")]
    [InlineData("Wget/1.21.4", "Bot", "Other", "bot")]
    [InlineData("python-requests/2.31.0", "Bot", "Other", "bot")]
    [InlineData("Go-http-client/2.0", "Bot", "Other", "bot")]
    [InlineData("Apache-HttpClient/4.5.14 (Java/17.0.9)", "Bot", "Other", "bot")]
    [InlineData("SOMECRAWLER/1.0", "Bot", "Other", "bot")]
    // Nothing usable
    [InlineData("", "Other", "Other", "unknown")]
    [InlineData("   ", "Other", "Other", "unknown")]
    [InlineData("???", "Other", "Other", "unknown")]
    [InlineData("Mozilla/5.0", "Other", "Other", "unknown")]
    public void Classifies_real_world_user_agents(string userAgent, string browser, string os, string device)
    {
        var info = UserAgentParser.Parse(userAgent);

        Assert.Equal(new ClientInfo(browser, os, device), info);
    }

    [Fact]
    public void Null_user_agent_is_unknown()
    {
        Assert.Equal(new ClientInfo("Other", "Other", "unknown"), UserAgentParser.Parse(null));
    }

    [Fact]
    public void Only_the_first_512_characters_are_inspected()
    {
        var padding = new string('x', UserAgentParser.MaxInspectedLength);

        // The token sits beyond the inspected prefix, so it must not influence the result (and must not be expensive to skip).
        Assert.Equal(new ClientInfo("Other", "Other", "unknown"), UserAgentParser.Parse(padding + " Firefox/1.0 Windows"));
        Assert.Equal("Firefox", UserAgentParser.Parse("Firefox/1.0 " + padding).Browser);
    }

    [Fact]
    public void Random_input_never_throws_and_stays_within_the_documented_vocabulary()
    {
        string[] browsers = ["Chrome", "Edge", "Firefox", "Safari", "Opera", "Samsung Internet", "Internet Explorer", "Bot", "Other"];
        string[] systems = ["Windows", "macOS", "iOS", "Android", "Linux", "ChromeOS", "Other"];
        string[] devices = ["desktop", "mobile", "tablet", "bot", "unknown"];
        string[] fragments =
        [
            "Mozilla/5.0", "Windows", "Android", "iPhone", "Mobile", "Safari/", "Chrome/", "Firefox/", "bot", "X11", "(", ")", ";",
            " ", "Macintosh", "like Gecko", "Edg/", "OPR/", "é", "\0", "Version/17", "Tablet", "Linux",
        ];

        var random = new Random(20260310);
        for (var i = 0; i < 2_000; i++)
        {
            var length = random.Next(0, 24);
            var userAgent = string.Concat(Enumerable.Range(0, length).Select(_ => fragments[random.Next(fragments.Length)]));

            var info = UserAgentParser.Parse(userAgent);

            Assert.Contains(info.Browser, browsers);
            Assert.Contains(info.Os, systems);
            Assert.Contains(info.Device, devices);
        }
    }
}
