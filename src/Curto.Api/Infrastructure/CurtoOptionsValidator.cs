using System.Globalization;
using Microsoft.Extensions.Options;

namespace Curto.Api.Infrastructure;

/// <summary>Fails fast at start-up when the configuration cannot work, with one message per problem.</summary>
/// <param name="environment">Decides whether an API key is mandatory.</param>
internal sealed class CurtoOptionsValidator(IHostEnvironment environment) : IValidateOptions<CurtoOptions>
{
    /// <summary>Shortest accepted API key.</summary>
    public const int MinApiKeyLength = 16;

    /// <summary>Longest accepted API key.</summary>
    public const int MaxApiKeyLength = 256;

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, CurtoOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> failures = [];

        ValidateApiKey(options.ApiKey, failures);
        ValidateBaseUrl(options.BaseUrl, failures);

        if (string.IsNullOrWhiteSpace(options.DbPath) || options.DbPath.Contains('\0', StringComparison.Ordinal))
        {
            failures.Add("Curto:DbPath (CURTO_DB_PATH) must be a file path.");
        }

        Range(failures, "Curto:RetentionDays (CURTO_RETENTION_DAYS)", options.RetentionDays, 1, 3650);
        Range(failures, "Curto:MaxRequestBodyBytes", options.MaxRequestBodyBytes, 1024, 1_048_576);
        Range(failures, "Curto:SseHeartbeatSeconds", options.SseHeartbeatSeconds, 1, 300);
        Range(failures, "Curto:SseMaxSubscribers", options.SseMaxSubscribers, 1, 10_000);
        Range(failures, "Curto:RateLimits:RedirectPerMinute (CURTO_RATE_LIMIT_REDIRECT_PER_MINUTE)", options.RateLimits.RedirectPerMinute, 1, 1_000_000);
        Range(failures, "Curto:RateLimits:CreatePerMinute (CURTO_RATE_LIMIT_CREATE_PER_MINUTE)", options.RateLimits.CreatePerMinute, 1, 1_000_000);
        Range(failures, "Curto:RateLimits:ApiPerMinute (CURTO_RATE_LIMIT_API_PER_MINUTE)", options.RateLimits.ApiPerMinute, 1, 1_000_000);
        Range(failures, "Curto:Clicks:QueueCapacity", options.Clicks.QueueCapacity, 16, 1_000_000);
        Range(failures, "Curto:Clicks:BatchSize", options.Clicks.BatchSize, 1, 10_000);
        Range(failures, "Curto:Clicks:FlushIntervalMs", options.Clicks.FlushIntervalMs, 1, 10_000);

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private void ValidateApiKey(string? apiKey, List<string> failures)
    {
        if (string.IsNullOrEmpty(apiKey))
        {
            if (!environment.IsDevelopment())
            {
                failures.Add(
                    "Curto:ApiKey (CURTO_API_KEY) is required unless ASPNETCORE_ENVIRONMENT is Development. " +
                    "Generate one with: openssl rand -base64 32");
            }

            return;
        }

        if (apiKey.Length is < MinApiKeyLength or > MaxApiKeyLength || apiKey.Any(c => c <= ' ' || c >= 0x7F))
        {
            failures.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"Curto:ApiKey (CURTO_API_KEY) must be {MinApiKeyLength}-{MaxApiKeyLength} printable ASCII characters without spaces."));
        }
    }

    private static void ValidateBaseUrl(string? baseUrl, List<string> failures)
    {
        if (string.IsNullOrEmpty(baseUrl))
        {
            return;
        }

        var valid = Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https"
            && uri.UserInfo.Length == 0
            && uri.Query.Length == 0
            && uri.Fragment.Length == 0
            && uri.AbsolutePath == "/";

        if (!valid)
        {
            failures.Add("Curto:BaseUrl (CURTO_BASE_URL) must be an absolute http(s) origin such as https://s.example.com (no path, query or credentials).");
        }
    }

    private static void Range(List<string> failures, string setting, int value, int min, int max)
    {
        if (value < min || value > max)
        {
            failures.Add(string.Create(CultureInfo.InvariantCulture, $"{setting} must be between {min} and {max}, but is {value}."));
        }
    }
}
