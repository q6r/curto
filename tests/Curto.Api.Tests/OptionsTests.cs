using Curto.Api.Infrastructure;
using Curto.Api.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;

namespace Curto.Api.Tests;

public sealed class OptionsTests
{
    private static string[] Failures(CurtoOptions options, string environment = "Production")
    {
        var result = new CurtoOptionsValidator(new FakeHostEnvironment(environment)).Validate(null, options);
        return result.Failures?.ToArray() ?? [];
    }

    private static CurtoOptions Valid() => new() { ApiKey = "0123456789abcdef-secret" };

    [Fact]
    public void Defaults_with_a_key_are_valid()
    {
        Assert.Empty(Failures(Valid()));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("anything-else")]
    public void A_key_is_required_outside_development(string environment)
    {
        var failures = Failures(new CurtoOptions(), environment);

        var failure = Assert.Single(failures);
        Assert.Contains("CURTO_API_KEY", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void Development_may_omit_the_key()
    {
        Assert.Empty(Failures(new CurtoOptions(), "Development"));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("123456789012345")] // 15 characters
    [InlineData("has a space in it 123")]
    [InlineData("tab\tseparated-key-123")]
    [InlineData("não-ascii-key-123456")]
    public void Weak_or_unusable_keys_are_rejected_everywhere(string key)
    {
        Assert.Single(Failures(new CurtoOptions { ApiKey = key }, "Development"));
    }

    [Fact]
    public void Overlong_keys_are_rejected()
    {
        Assert.Single(Failures(new CurtoOptions { ApiKey = new string('k', CurtoOptionsValidator.MaxApiKeyLength + 1) }));
    }

    [Theory]
    [InlineData("https://s.example.com")]
    [InlineData("https://s.example.com/")]
    [InlineData("http://localhost:8080")]
    [InlineData("")]
    [InlineData(null)]
    public void Accepts_an_origin_or_nothing_as_base_url(string? baseUrl)
    {
        var options = Valid();
        options.BaseUrl = baseUrl;

        Assert.Empty(Failures(options));
    }

    [Theory]
    [InlineData("s.example.com")]
    [InlineData("ftp://s.example.com")]
    [InlineData("https://s.example.com/prefix")]
    [InlineData("https://s.example.com/?x=1")]
    [InlineData("https://s.example.com/#frag")]
    [InlineData("https://user:pw@s.example.com")]
    [InlineData("javascript:alert(1)")]
    public void Rejects_a_base_url_that_is_not_a_bare_origin(string baseUrl)
    {
        var options = Valid();
        options.BaseUrl = baseUrl;

        var failure = Assert.Single(Failures(options));
        Assert.Contains("CURTO_BASE_URL", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void Numeric_settings_are_range_checked_and_all_problems_are_reported_together()
    {
        var options = Valid();
        options.RetentionDays = 0;
        options.RateLimits.CreatePerMinute = -1;
        options.Clicks.QueueCapacity = 5;
        options.SseHeartbeatSeconds = 1000;

        var failures = Failures(options);

        Assert.Equal(4, failures.Length);
        Assert.Contains(failures, f => f.Contains("RetentionDays", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.Contains("CreatePerMinute", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.Contains("QueueCapacity", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.Contains("SseHeartbeatSeconds", StringComparison.Ordinal));
    }

    [Fact]
    public void Database_path_must_not_be_blank()
    {
        var options = Valid();
        options.DbPath = "  ";

        Assert.Contains(Failures(options), f => f.Contains("CURTO_DB_PATH", StringComparison.Ordinal));
    }

    [Fact]
    public void Environment_aliases_override_files_but_not_the_command_line()
    {
        var variables = new Dictionary<string, string?>
        {
            ["CURTO_BASE_URL"] = "https://from-env.example",
            ["CURTO_DB_PATH"] = "  /data/curto.db  ",
            ["CURTO_BLOCK_PRIVATE_TARGETS"] = "true",
            ["CURTO_RATE_LIMIT_CREATE_PER_MINUTE"] = "7",
            ["CURTO_RETENTION_DAYS"] = "",
        };

        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Curto:BaseUrl"] = "https://from-file.example",
            ["Curto:RetentionDays"] = "90",
            ["Curto:RateLimits:CreatePerMinute"] = "20",
        });
        configuration.AddCommandLine(["--Curto:RateLimits:CreatePerMinute=99"]);

        configuration.AddCurtoEnvironmentAliases(name => variables.GetValueOrDefault(name));

        var options = configuration.GetSection(CurtoOptions.SectionName).Get<CurtoOptions>()!;
        Assert.Equal("https://from-env.example", options.BaseUrl);          // alias beats the file
        Assert.Equal("/data/curto.db", options.DbPath);                     // trimmed
        Assert.True(options.BlockPrivateTargets);                           // boolean parsing
        Assert.Equal(90, options.RetentionDays);                            // empty alias is ignored
        Assert.Equal(99, options.RateLimits.CreatePerMinute);               // command line beats the alias
    }

    [Fact]
    public void Aliases_still_beat_appsettings_when_the_host_starts_with_command_line_arguments()
    {
        // WebApplication.CreateBuilder(args) registers the command line twice: with the host configuration, and again after
        // the default sources. The alias source has to go below the last one, or it would rank below appsettings.json
        // (whose empty "ApiKey" would then win) as soon as the process is started with any argument, for example --urls.
        var directory = Directory.CreateTempSubdirectory("curto-config-");
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "appsettings.json"), """{ "Curto": { "ApiKey": "", "RetentionDays": 180 } }""");
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = ["--Curto:RetentionDays=90"], ContentRootPath = directory.FullName });

            // Disposing the configuration releases the file watcher on appsettings.json, which would block deleting the folder.
            using var configuration = builder.Configuration;
            configuration.AddCurtoEnvironmentAliases(name => name == "CURTO_API_KEY" ? "alias-key-0123456789" : null);

            Assert.Equal("alias-key-0123456789", configuration["Curto:ApiKey"]); // beats the empty value in appsettings.json
            Assert.Equal("90", configuration["Curto:RetentionDays"]);            // the command line still beats appsettings.json
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Without_any_alias_nothing_changes()
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Curto:BaseUrl"] = "https://file.example" });
        var sourcesBefore = configuration.Sources.Count;

        configuration.AddCurtoEnvironmentAliases(_ => null);

        Assert.Equal(sourcesBefore, configuration.Sources.Count);
        Assert.Equal("https://file.example", configuration["Curto:BaseUrl"]);
    }
}
