using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Curto.Api.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Curto.Api.Tests.Support;

/// <summary>
/// Hosts the real application in memory with an isolated SQLite file, a controllable clock and fast click flushing.
/// Derive from it to change settings; use one instance per test class (xUnit class fixture).
/// </summary>
public class CurtoFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    /// <summary>API key every test client sends.</summary>
    public const string ApiKey = "test-api-key-0123456789abcdef";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "curto-tests", Guid.NewGuid().ToString("N"));

    /// <summary>Clock injected into the application. It only moves when a test advances it.</summary>
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 3, 15, 10, 30, 0, TimeSpan.Zero));

    /// <summary>Path of the SQLite file this host uses.</summary>
    public string DatabasePath => Path.Combine(_directory, "curto.db");

    /// <summary>Environment name the host runs in.</summary>
    protected virtual string EnvironmentName => "Development";

    /// <summary>Whether the host is configured with <see cref="ApiKey"/>.</summary>
    protected virtual bool ConfigureApiKey => true;

    /// <summary>Hook for subclasses to override configuration values (key form: <c>Curto:RateLimits:CreatePerMinute</c>).</summary>
    protected virtual void Configure(IDictionary<string, string?> settings)
    {
    }

    /// <summary>Hook for subclasses to replace services.</summary>
    protected virtual void ConfigureTestServices(IServiceCollection services)
    {
    }

    /// <summary>Runs once after the fixture is created; subclasses override it to seed data.</summary>
    public virtual Task InitializeAsync() => Task.CompletedTask;

    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    /// <summary>A client that does not follow redirects and (by default) sends the API key.</summary>
    /// <param name="withKey">False to send no key.</param>
    public HttpClient CreateApiClient(bool withKey = true)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (withKey)
        {
            client.DefaultRequestHeaders.Add("X-API-Key", ApiKey);
        }

        return client;
    }

    /// <summary>Creates a link through the API and returns the response body.</summary>
    /// <param name="client">Authenticated client.</param>
    /// <param name="url">Target URL.</param>
    /// <param name="configure">Adds optional members (slug, title, tags, expiresAt, maxClicks).</param>
    public static async Task<JsonElement> CreateLinkAsync(HttpClient client, string url = "https://example.com/", Action<Dictionary<string, object?>>? configure = null)
    {
        var body = new Dictionary<string, object?> { ["url"] = url };
        configure?.Invoke(body);

        using var response = await client.PostAsJsonAsync("/api/links", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Waits until every queued click has been written (or accounted for as dropped/failed).</summary>
    public async Task DrainClicksAsync()
    {
        var channel = Services.GetRequiredService<ClickChannel>();
        await Eventually.Async(() =>
        {
            var stats = channel.Snapshot();
            return stats.Persisted + stats.Failed + stats.Dropped >= stats.Enqueued && stats.Depth == 0;
        });
    }

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        Dictionary<string, string?> settings = new()
        {
            ["Curto:DbPath"] = DatabasePath,
            ["Curto:Clicks:FlushIntervalMs"] = "10",
            ["Curto:SseHeartbeatSeconds"] = "1",
            ["Curto:RateLimits:RedirectPerMinute"] = "100000",
            ["Curto:RateLimits:CreatePerMinute"] = "100000",
            ["Curto:RateLimits:ApiPerMinute"] = "100000",
        };
        if (ConfigureApiKey)
        {
            settings["Curto:ApiKey"] = ApiKey;
        }

        Configure(settings);

        builder.UseEnvironment(EnvironmentName);
        foreach (var (key, value) in settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            services.AddSingleton<IStartupFilter, RemoteIpStartupFilter>();
            ConfigureTestServices(services);
        });
    }

    /// <summary>Set to inspect the database after the host has stopped; the test then calls <see cref="DeleteTemporaryFiles"/>.</summary>
    public bool KeepFilesOnDispose { get; set; }

    /// <summary>Deletes the temporary directory holding the database. Safe to call repeatedly.</summary>
    public void DeleteTemporaryFiles()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup of a temp folder; a leftover directory must not fail the test run.
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing && !KeepFilesOnDispose)
        {
            DeleteTemporaryFiles();
        }
    }
}

/// <summary>Lets a test pretend to be a given client address through the <c>X-Test-Ip</c> request header.</summary>
internal sealed class RemoteIpStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.Use(async (HttpContext context, Func<Task> pipeline) =>
            {
                if (context.Request.Headers.TryGetValue("X-Test-Ip", out var value) && IPAddress.TryParse(value.ToString(), out var address))
                {
                    context.Connection.RemoteIpAddress = address;
                }

                await pipeline();
            });
            next(app);
        };
    }
}
