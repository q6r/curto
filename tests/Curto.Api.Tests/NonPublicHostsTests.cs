using System.Net;
using Curto.Api.Services;

namespace Curto.Api.Tests;

public sealed class NonPublicHostsTests
{
    [Theory]
    // Names
    [InlineData("http://localhost/")]
    [InlineData("http://LOCALHOST:8080/admin")]
    [InlineData("http://app.localhost/")]
    [InlineData("http://printer.local/")]
    [InlineData("http://db.internal/")]
    [InlineData("http://nas.lan/")]
    [InlineData("http://x.home.arpa/")]
    [InlineData("http://intranet/")]
    [InlineData("http://localhost./")]
    // IPv4
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://127.255.255.254/")]
    [InlineData("http://0.0.0.0/")]
    [InlineData("http://10.0.0.1/")]
    [InlineData("http://172.16.0.1/")]
    [InlineData("http://172.31.255.255/")]
    [InlineData("http://192.168.1.1/")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://100.64.0.1/")]
    [InlineData("http://100.127.255.255/")]
    [InlineData("http://198.18.0.1/")]
    [InlineData("http://192.0.0.8/")]
    [InlineData("http://224.0.0.1/")]
    [InlineData("http://255.255.255.255/")]
    // Alternative spellings that the URI parser normalises to an IPv4 address
    [InlineData("http://2130706433/")]
    [InlineData("http://0x7f.0.0.1/")]
    [InlineData("http://0177.0.0.1/")]
    [InlineData("http://127.1/")]
    // IPv6, including addresses that embed IPv4
    [InlineData("http://[::1]/")]
    [InlineData("http://[::]/")]
    [InlineData("http://[fe80::1]/")]
    [InlineData("http://[fc00::1]/")]
    [InlineData("http://[fd12:3456:789a::1]/")]
    [InlineData("http://[ff02::1]/")]
    [InlineData("http://[::ffff:127.0.0.1]/")]
    [InlineData("http://[::ffff:10.1.2.3]/")]
    [InlineData("http://[::127.0.0.1]/")]
    [InlineData("http://[64:ff9b::7f00:1]/")]
    [InlineData("http://[2002:7f00:1::]/")]
    public void Flags_non_public_targets(string url)
    {
        Assert.True(NonPublicHosts.Matches(new Uri(url)), url);
    }

    [Theory]
    [InlineData("https://example.com/")]
    [InlineData("https://sub.domain.example.org/path")]
    [InlineData("http://8.8.8.8/")]
    [InlineData("http://1.1.1.1/")]
    [InlineData("http://172.15.255.255/")]
    [InlineData("http://172.32.0.1/")]
    [InlineData("http://192.167.1.1/")]
    [InlineData("http://192.169.0.1/")]
    [InlineData("http://100.63.255.255/")]
    [InlineData("http://100.128.0.1/")]
    [InlineData("http://169.253.0.1/")]
    [InlineData("http://198.17.0.1/")]
    [InlineData("http://198.20.0.1/")]
    [InlineData("http://[2606:4700:4700::1111]/")]
    [InlineData("http://[::ffff:8.8.8.8]/")]
    [InlineData("http://[64:ff9b::808:808]/")]
    [InlineData("https://locally.example.com/")]
    [InlineData("https://notlocalhost.example.com/")]
    public void Lets_public_targets_through(string url)
    {
        Assert.False(NonPublicHosts.Matches(new Uri(url)), url);
    }

    [Theory]
    [InlineData("10.255.255.255", true)]
    [InlineData("11.0.0.0", false)]
    [InlineData("172.16.0.0", true)]
    [InlineData("172.15.255.255", false)]
    [InlineData("192.168.0.0", true)]
    [InlineData("::1", true)]
    [InlineData("2001:4860:4860::8888", false)]
    public void Classifies_raw_addresses(string address, bool expected)
    {
        Assert.Equal(expected, NonPublicHosts.IsNonPublic(IPAddress.Parse(address)));
    }
}
