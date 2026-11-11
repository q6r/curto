using Microsoft.Extensions.Configuration.CommandLine;

namespace Curto.Api.Infrastructure;

/// <summary>
/// Maps the friendly <c>CURTO_*</c> environment variables onto the <c>Curto</c> configuration section.
/// The generic <c>Curto__Name</c> form keeps working through the standard environment provider.
/// </summary>
internal static class EnvironmentConfiguration
{
    private static readonly (string Variable, string Key)[] Aliases =
    [
        ("CURTO_BASE_URL", "Curto:BaseUrl"),
        ("CURTO_DB_PATH", "Curto:DbPath"),
        ("CURTO_API_KEY", "Curto:ApiKey"),
        ("CURTO_BLOCK_PRIVATE_TARGETS", "Curto:BlockPrivateTargets"),
        ("CURTO_RETENTION_DAYS", "Curto:RetentionDays"),
        ("CURTO_RATE_LIMIT_REDIRECT_PER_MINUTE", "Curto:RateLimits:RedirectPerMinute"),
        ("CURTO_RATE_LIMIT_CREATE_PER_MINUTE", "Curto:RateLimits:CreatePerMinute"),
        ("CURTO_RATE_LIMIT_API_PER_MINUTE", "Curto:RateLimits:ApiPerMinute"),
    ];

    /// <summary>
    /// Adds the alias source just below the command line, so the precedence stays
    /// appsettings &lt; environment &lt; command line. The host registers the command line twice when the process is
    /// started with arguments (with the host configuration and again after the default sources), so the position is
    /// taken from the last registration: the first one sits below appsettings.json.
    /// </summary>
    /// <param name="configuration">The application configuration.</param>
    /// <param name="getVariable">Environment lookup; defaults to the process environment.</param>
    public static void AddCurtoEnvironmentAliases(this ConfigurationManager configuration, Func<string, string?>? getVariable = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        getVariable ??= Environment.GetEnvironmentVariable;

        Dictionary<string, string?> values = [];
        foreach (var (variable, key) in Aliases)
        {
            var value = getVariable(variable);
            if (!string.IsNullOrWhiteSpace(value))
            {
                values[key] = value.Trim();
            }
        }

        if (values.Count == 0)
        {
            return;
        }

        var source = new Microsoft.Extensions.Configuration.Memory.MemoryConfigurationSource { InitialData = values };
        var index = configuration.Sources.ToList().FindLastIndex(s => s is CommandLineConfigurationSource);
        configuration.Sources.Insert(index < 0 ? configuration.Sources.Count : index, source);
    }
}
