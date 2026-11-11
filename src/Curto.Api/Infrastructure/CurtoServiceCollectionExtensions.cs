using System.Text.Json;
using System.Text.Json.Serialization;
using Curto.Api.Services;
using Curto.Api.Storage;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Curto.Api.Infrastructure;

/// <summary>Service registrations for the whole application.</summary>
internal static class CurtoServiceCollectionExtensions
{
    /// <summary>Registers options, security, persistence, the click pipeline and the HTTP plumbing.</summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Application configuration (live view: values added later by the host still apply).</param>
    public static IServiceCollection AddCurto(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Configuration. Bound lazily from the live configuration; validated when the host starts.
        services.AddSingleton<IValidateOptions<CurtoOptions>, CurtoOptionsValidator>();
        services.AddOptions<CurtoOptions>()
            .Bind(configuration.GetSection(CurtoOptions.SectionName))
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);

        services.AddHttpPlumbing();
        services.AddSecurity();
        services.AddCurtoRateLimiting();
        services.AddCurtoOpenApi();

        // Storage and domain.
        services.AddSingleton<Database>();
        services.AddSingleton<LinkRepository>();
        services.AddSingleton<ClickStore>();
        services.AddSingleton<StatsRepository>();

        services.AddSingleton<ISlugGenerator, SlugGenerator>();
        services.AddSingleton<TargetUrlValidator>();
        services.AddSingleton<LinkRequestValidator>();
        services.AddSingleton<LinkService>();
        services.AddSingleton<LinkMapper>();
        services.AddSingleton<RedirectService>();
        services.AddSingleton<StatsService>();
        services.AddSingleton<VisitorHasher>();

        // Click pipeline: bounded channel -> batching writer -> SQLite, with a live fan-out on the side.
        services.AddSingleton<ClickChannel>();
        services.AddSingleton<EventBroadcaster>();
        services.AddHostedService<ClickWriterService>();
        services.AddSingleton<RetentionService>();
        services.AddHostedService(provider => provider.GetRequiredService<RetentionService>());

        return services;
    }

    private static void AddHttpPlumbing(this IServiceCollection services)
    {
        services.ConfigureHttpJsonOptions(options =>
        {
            var json = options.SerializerOptions;
            json.NumberHandling = JsonNumberHandling.Strict; // the "web" defaults would accept "5" for a number
            json.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
            json.Converters.Add(new UtcDateTimeOffsetConverter());
        });

        // Malformed bodies and bad parameters throw BadHttpRequestException in every environment, so the exception
        // handler answers them with one consistent problem document (by default this only happens in Development).
        services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
        services.Configure<RouteOptions>(options => options.ConstraintMap["slug"] = typeof(SlugRouteConstraint));

        services.AddProblemDetails(options =>
            options.CustomizeProblemDetails = context => Problems.Enrich(context.HttpContext, context.ProblemDetails));
        services.AddSingleton<IProblemDetailsWriter, JsonProblemWriter>();
        services.AddExceptionHandler<GlobalExceptionHandler>();

        services.AddOptions<KestrelServerOptions>().Configure<IOptions<CurtoOptions>>((kestrel, curto) =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = curto.Value.MaxRequestBodyBytes;
            kestrel.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
            kestrel.Limits.MaxRequestHeaderCount = 64;
            kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
        });
    }

    private static void AddSecurity(this IServiceCollection services)
    {
        services.AddSingleton<ApiKeyVerifier>();

        // AddAuthenticationCore instead of AddAuthentication: the latter registers ASP.NET Data Protection, which would
        // create a key ring on disk at start-up. An API-key scheme has no cookies or tokens to protect.
        services.AddAuthenticationCore(options => options.AddScheme<ApiKeyAuthenticationHandler>(ApiKeyDefaults.Scheme, displayName: null));
        services.AddWebEncoders();
        services.AddAuthorizationBuilder()
            .AddPolicy(ApiKeyDefaults.Policy, policy => policy
                .AddAuthenticationSchemes(ApiKeyDefaults.Scheme)
                .RequireAuthenticatedUser());
    }
}
