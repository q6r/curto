using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Curto.Api.Infrastructure;

/// <summary>Names of the rate-limit policies attached to endpoints.</summary>
internal static class RateLimitPolicies
{
    /// <summary>Redirects and QR codes.</summary>
    public const string Redirect = "redirect";

    /// <summary>Link creation.</summary>
    public const string Create = "create";

    /// <summary>All other management endpoints.</summary>
    public const string Api = "api";
}

/// <summary>Fixed-window, per-client rate limiting driven by <see cref="CurtoOptions"/>.</summary>
internal static class RateLimiting
{
    /// <summary>Registers the policies and the problem-document rejection response.</summary>
    /// <param name="services">Service collection.</param>
    public static IServiceCollection AddCurtoRateLimiting(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = static (context, cancellationToken) =>
            {
                TimeSpan? retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var wait) ? wait : null;
                return new ValueTask(Problems.TooManyRequests(retryAfter).ExecuteAsync(context.HttpContext));
            };

            // Every policy owns its own limiter, so the same client has an independent budget per policy.
            options.AddPolicy(RateLimitPolicies.Redirect, http => Partition(http, s => s.RedirectPerMinute));
            options.AddPolicy(RateLimitPolicies.Create, http => Partition(http, s => s.CreatePerMinute));
            options.AddPolicy(RateLimitPolicies.Api, http => Partition(http, s => s.ApiPerMinute));
        });
    }

    private static RateLimitPartition<string> Partition(HttpContext http, Func<RateLimitSettings, int> permits)
    {
        // Settings are read per request (not captured at registration) so the final, validated configuration applies.
        var settings = http.RequestServices.GetRequiredService<IOptions<CurtoOptions>>().Value.RateLimits;

        return RateLimitPartition.GetFixedWindowLimiter(
            ClientAddress.RateLimitKey(ClientAddress.Of(http)),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permits(settings),
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            });
    }
}
