using System.Net;
using Curto.Api.Models;
using Curto.Api.Services;
using Curto.Api.Storage;
using Curto.Api.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Curto.Api.Tests;

public sealed class RetentionFactory : CurtoFactory
{
    protected override void Configure(IDictionary<string, string?> settings) => settings["Curto:RetentionDays"] = "30";
}

public sealed class ShutdownAndRetentionTests : IClassFixture<RetentionFactory>, IDisposable
{
    private readonly RetentionFactory _factory;
    private readonly HttpClient _client;

    public ShutdownAndRetentionTests(RetentionFactory factory)
    {
        _factory = factory;
        _client = factory.CreateApiClient();
    }

    public void Dispose() => _client.Dispose();

    private static ClickEvent Click(string slug, DateTimeOffset at, long visitor = 1) =>
        new(slug, at, visitor, null, "Chrome", "Windows", "desktop", Counted: true);

    private static async Task<long> CountAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Retention_removes_only_clicks_older_than_the_configured_days()
    {
        // Clock: 2026-03-15T10:30:00Z, retention 30 days, so the cutoff is 2026-02-13T10:30:00Z.
        var now = _factory.Clock.GetUtcNow();
        var cutoff = now.AddDays(-30);
        await CurtoFactory.CreateLinkAsync(_client, "https://example.com/retention", body => body["slug"] = "retention-link");
        var store = _factory.Services.GetRequiredService<ClickStore>();
        await store.InsertBatchAsync(
        [
            Click("retention-link", cutoff.AddSeconds(-1)),           // one second too old: removed
            Click("retention-link", cutoff.AddDays(-100), visitor: 2), // far too old: removed
            Click("retention-link", cutoff),                           // exactly at the cutoff: kept (strictly older rows go)
            Click("retention-link", cutoff.AddSeconds(1)),
            Click("retention-link", now.AddHours(-1)),
        ], CancellationToken.None);

        var removed = await _factory.Services.GetRequiredService<RetentionService>().PruneOnceAsync(CancellationToken.None);

        Assert.Equal(2, removed);
        Assert.Equal(3, await CountAsync(_factory.DatabasePath, "SELECT COUNT(*) FROM clicks WHERE slug = 'retention-link'"));
        Assert.Equal(0, await CountAsync(_factory.DatabasePath, $"SELECT COUNT(*) FROM clicks WHERE at < {cutoff.ToUnixTimeSeconds()}"));
    }

    [Fact]
    public async Task Retention_never_touches_links_or_their_lifetime_counters()
    {
        var now = _factory.Clock.GetUtcNow();
        await CurtoFactory.CreateLinkAsync(_client, "https://example.com/counters", body => body["slug"] = "counter-link");
        for (var i = 0; i < 3; i++)
        {
            using var visit = await _client.GetAsync("/counter-link");
        }

        await _factory.DrainClicksAsync();
        await _factory.Services.GetRequiredService<ClickStore>().InsertBatchAsync([Click("counter-link", now.AddDays(-400))], CancellationToken.None);

        await _factory.Services.GetRequiredService<RetentionService>().PruneOnceAsync(CancellationToken.None);

        using var get = await _client.GetAsync("/api/links/counter-link");
        var link = await get.JsonAsync();
        Assert.Equal(3, link.Num("clicks")); // lifetime total, independent of how many rows are kept
        Assert.Equal(3, await CountAsync(_factory.DatabasePath, "SELECT COUNT(*) FROM clicks WHERE slug = 'counter-link'"));
    }

    [Fact]
    public async Task Pruning_a_large_backlog_works_in_chunks()
    {
        var now = _factory.Clock.GetUtcNow();
        await CurtoFactory.CreateLinkAsync(_client, "https://example.com/backlog", body => body["slug"] = "backlog-link");
        var store = _factory.Services.GetRequiredService<ClickStore>();
        var old = Enumerable.Range(0, 12_000).Select(i => Click("backlog-link", now.AddDays(-60).AddSeconds(i), i)).ToArray();
        await store.InsertBatchAsync(old, CancellationToken.None);
        await store.InsertBatchAsync([Click("backlog-link", now)], CancellationToken.None);

        var removed = await _factory.Services.GetRequiredService<RetentionService>().PruneOnceAsync(CancellationToken.None);

        Assert.Equal(12_000, removed); // more than the 5,000-row chunk size
        Assert.Equal(1, await CountAsync(_factory.DatabasePath, "SELECT COUNT(*) FROM clicks WHERE slug = 'backlog-link'"));
    }

    [Fact]
    public async Task Pruning_when_there_is_nothing_to_prune_is_harmless()
    {
        var removed = await _factory.Services.GetRequiredService<RetentionService>().PruneOnceAsync(CancellationToken.None);

        Assert.True(removed >= 0);
    }

    [Fact]
    public async Task Queued_clicks_are_written_when_the_host_shuts_down()
    {
        var factory = new CurtoFactory { KeepFilesOnDispose = true };
        try
        {
            using (var client = factory.CreateApiClient())
            {
                await CurtoFactory.CreateLinkAsync(client, "https://example.com/shutdown", body => body["slug"] = "shutdown-link");
            }

            var channel = factory.Services.GetRequiredService<ClickChannel>();

            // Far more than one batch, queued faster than the writer could flush them.
            for (var i = 0; i < 1_500; i++)
            {
                channel.TryEnqueue(new ClickEvent("shutdown-link", factory.Clock.GetUtcNow(), i, null, "Chrome", "Windows", "desktop", Counted: false));
            }

            await factory.DisposeAsync(); // stops the host: the writer drains the channel first

            Assert.Equal(1_500, await CountAsync(factory.DatabasePath, "SELECT COUNT(*) FROM clicks WHERE slug = 'shutdown-link'"));
            Assert.Equal(1_500, await CountAsync(factory.DatabasePath, "SELECT clicks FROM links WHERE slug = 'shutdown-link'"));
        }
        finally
        {
            factory.DeleteTemporaryFiles();
        }
    }

    [Fact]
    public async Task A_burst_of_redirects_is_fully_recorded_in_batches()
    {
        await CurtoFactory.CreateLinkAsync(_client, "https://example.com/burst", body => body["slug"] = "burst-link");

        var statuses = await Task.WhenAll(Enumerable.Range(0, 300).Select(async i =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/burst-link");
            request.Headers.Add("X-Test-Ip", $"198.51.100.{i % 250}");
            using var response = await _client.SendAsync(request);
            return response.StatusCode;
        }));
        await _factory.DrainClicksAsync();

        Assert.All(statuses, status => Assert.Equal(HttpStatusCode.Found, status));
        Assert.Equal(300, await CountAsync(_factory.DatabasePath, "SELECT COUNT(*) FROM clicks WHERE slug = 'burst-link'"));
        Assert.Equal(300, await CountAsync(_factory.DatabasePath, "SELECT clicks FROM links WHERE slug = 'burst-link'"));
        var stats = _factory.Services.GetRequiredService<ClickChannel>().Snapshot();
        Assert.Equal(0, stats.Dropped);
        Assert.Equal(0, stats.Failed);
    }
}
