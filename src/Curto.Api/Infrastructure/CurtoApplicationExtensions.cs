using Curto.Api.Endpoints;
using Curto.Api.Storage;
using Scalar.AspNetCore;

namespace Curto.Api.Infrastructure;

/// <summary>Request pipeline, endpoint mapping and start-up tasks.</summary>
internal static class CurtoApplicationExtensions
{
    /// <summary>Creates the database schema, then (Development only) prints the generated API key once.</summary>
    /// <param name="app">Built application.</param>
    /// <param name="cancellationToken">Cancels the migration.</param>
    public static async Task InitialiseAsync(this WebApplication app, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(app);

        await app.Services.GetRequiredService<Database>().MigrateAsync(cancellationToken);

        if (app.Services.GetRequiredService<ApiKeyVerifier>().GeneratedKey is { } key)
        {
            // Console only, deliberately not the logging pipeline: secrets must not reach log sinks.
            Console.WriteLine($"[curto] No CURTO_API_KEY configured. Development API key for this run (shown once): {key}");
        }
    }

    /// <summary>
    /// Adds the middleware in dependency order: correlation id and error handling wrap everything; static files are served
    /// before routing so the <c>/{slug}</c> route can never shadow them; rate limiting and authorisation need the routed endpoint.
    /// </summary>
    /// <param name="app">Built application.</param>
    public static WebApplication UseCurto(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseMiddleware<SecurityHeadersMiddleware>();
        app.UseMiddleware<RequestLimitsMiddleware>();

        app.UseDefaultFiles();
        app.UseStaticFiles(new StaticFileOptions
        {
            // Revalidate on every load (cheap: ETag + 304) so an upgrade never serves a stale dashboard.
            OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache",
        });

        app.UseRouting();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();
        return app;
    }

    /// <summary>Maps every endpoint. The slug redirect goes last so that more specific routes win.</summary>
    /// <param name="app">Built application.</param>
    public static WebApplication MapCurto(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapOpenApi();
        app.MapScalarApiReference("/docs", (options, http) =>
        {
            options.WithTitle("Curto API");

            // The API reference must work offline and must not phone home.
            options.Telemetry = false;
            options.DefaultFonts = false;
            options.DisableAgent();
            options.DisableMcp();
            options.AddPreferredSecuritySchemes(ApiKeyDefaults.Scheme);

            if (http.Items[SecurityHeadersMiddleware.NonceItemKey] is string nonce)
            {
                options.WithNonce(nonce);
            }
        });

        app.MapHealthEndpoints();

        var api = app.MapGroup("/api")
            .RequireAuthorization(ApiKeyDefaults.Policy)
            .RequireRateLimiting(RateLimitPolicies.Api);
        api.MapLinkEndpoints();
        api.MapStatsEndpoints();
        api.MapEventsEndpoints();

        app.MapQrEndpoints();
        app.MapRedirectEndpoints();
        return app;
    }
}
