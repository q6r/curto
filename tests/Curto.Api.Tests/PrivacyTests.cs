using System.Net;
using System.Text;
using Curto.Api.Tests.Support;
using Microsoft.Data.Sqlite;

namespace Curto.Api.Tests;

/// <summary>The privacy promises made in docs/privacy.md, checked against the bytes on disk.</summary>
public sealed class PrivacyTests : IClassFixture<CurtoFactory>, IDisposable
{
    private const string ClientAddress = "203.0.113.77";
    private const string ClientAddressV6 = "2001:db8:dead:beef::77";
    private const string UniqueAgent = "PrivacyProbe/9.9 (UniqueTokenXyz; secret-device-id-4711)";
    private const string Referer = "https://www.Referrer-Site.example:8443/private/path?token=s3cr3t#section";

    private readonly CurtoFactory _factory;
    private readonly HttpClient _client;

    public PrivacyTests(CurtoFactory factory)
    {
        _factory = factory;
        _client = factory.CreateApiClient();
    }

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task No_address_user_agent_or_referrer_path_is_ever_written_to_disk()
    {
        await CurtoFactory.CreateLinkAsync(_client, "https://example.com/privacy", body =>
        {
            body["slug"] = "privacy-probe";
            body["title"] = "control-marker-4242"; // proves that the scan below can find strings that are stored
        });

        foreach (var address in new[] { ClientAddress, ClientAddressV6 })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/privacy-probe");
            request.Headers.Add("X-Test-Ip", address);
            request.Headers.TryAddWithoutValidation("User-Agent", UniqueAgent);
            request.Headers.TryAddWithoutValidation("Referer", Referer);
            using var response = await _client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        }

        await _factory.DrainClicksAsync();

        var disk = ReadDatabaseFiles();
        Assert.True(Contains(disk, "control-marker-4242"), "the scan must be able to find stored text");
        Assert.True(Contains(disk, "referrer-site.example"), "the referrer host is stored");

        // Long, distinctive needles only: short ones could match binary page data by coincidence.
        foreach (var secret in new[]
        {
            ClientAddress, ClientAddressV6, "dead:beef",
            "PrivacyProbe", "UniqueTokenXyz", "secret-device-id",
            "private/path", "token=s3cr3t", "s3cr3t", "www.referrer",
        })
        {
            Assert.False(Contains(disk, secret), $"'{secret}' must not be stored");
        }
    }

    [Fact]
    public async Task Only_derived_values_are_stored_per_click()
    {
        await CurtoFactory.CreateLinkAsync(_client, "https://example.com/columns", body => body["slug"] = "columns-probe");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/columns-probe");
        request.Headers.Add("X-Test-Ip", ClientAddress);
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");
        request.Headers.TryAddWithoutValidation("Referer", "https://www.Referrer-Site.example/some/page?q=1");
        using var response = await _client.SendAsync(request);
        await _factory.DrainClicksAsync();

        await using var connection = new SqliteConnection($"Data Source={_factory.DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM clicks WHERE slug = 'columns-probe'";
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        var columns = Enumerable.Range(0, reader.FieldCount).ToDictionary(reader.GetName, i => reader.IsDBNull(i) ? null : reader.GetValue(i));

        Assert.Equal(["id", "slug", "at", "visitor", "referrer", "browser", "os", "device"], columns.Keys.ToArray());
        Assert.Equal("referrer-site.example", columns["referrer"]);
        Assert.Equal("Chrome", columns["browser"]);
        Assert.Equal("Windows", columns["os"]);
        Assert.Equal("desktop", columns["device"]);
        Assert.Equal(1773570600L, columns["at"]); // 2026-03-15T10:30:00Z in whole seconds: no sub-second timing detail
        Assert.IsType<long>(columns["visitor"]);
        Assert.NotEqual(0L, columns["visitor"]);
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task Visitors_are_distinguished_without_storing_who_they_are()
    {
        await CurtoFactory.CreateLinkAsync(_client, "https://example.com/visitors", body => body["slug"] = "visitors-probe");

        async Task VisitAsync(string ip, string agent)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/visitors-probe");
            request.Headers.Add("X-Test-Ip", ip);
            request.Headers.TryAddWithoutValidation("User-Agent", agent);
            using var response = await _client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        }

        await VisitAsync("198.51.100.1", "AgentA/1");
        await VisitAsync("198.51.100.1", "AgentA/1"); // same visitor again
        await VisitAsync("198.51.100.2", "AgentA/1"); // other address
        await VisitAsync("198.51.100.1", "AgentB/1"); // other browser
        await _factory.DrainClicksAsync();

        await using var connection = new SqliteConnection($"Data Source={_factory.DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*), COUNT(DISTINCT visitor) FROM clicks WHERE slug = 'visitors-probe'";
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();

        Assert.Equal(4, reader.GetInt64(0));
        Assert.Equal(3, reader.GetInt64(1));
    }

    private string[] DatabaseFiles() =>
        [_factory.DatabasePath, _factory.DatabasePath + "-wal"];

    private byte[][] ReadDatabaseFiles() =>
        [.. DatabaseFiles().Where(File.Exists).Select(path =>
        {
            // The server holds these files open, so read them with a shared handle.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        })];

    private static bool Contains(byte[][] files, string text)
    {
        // Text may be stored as UTF-8 or UTF-16; look for both, case-insensitively for ASCII.
        foreach (var encoding in new Encoding[] { Encoding.UTF8, Encoding.Unicode })
        {
            var needle = encoding.GetBytes(text);
            var lower = encoding.GetBytes(text.ToLowerInvariant());
            foreach (var file in files)
            {
                if (file.AsSpan().IndexOf(needle) >= 0 || file.AsSpan().IndexOf(lower) >= 0)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
