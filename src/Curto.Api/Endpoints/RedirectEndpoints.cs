using Curto.Api.Infrastructure;
using Curto.Api.Services;

namespace Curto.Api.Endpoints;

/// <summary>The public redirect: <c>GET /{slug}</c>.</summary>
internal static class RedirectEndpoints
{
    /// <summary>Maps the redirect. Register it after every more specific route.</summary>
    /// <param name="app">Route builder.</param>
    public static void MapRedirectEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/{slug:slug}", (string slug, HttpContext http, RedirectService redirects, CancellationToken cancellationToken) =>
                ResolveAsync(slug, http, redirects, recordClick: true, cancellationToken))
            .WithTags("Redirect")
            .WithName("Redirect")
            .WithSummary("Redirect to the target URL")
            .WithDescription("Answers 302 with Cache-Control: no-store, 404 for unknown slugs and 410 for expired or exhausted links. The click is recorded asynchronously.")
            .RequireRateLimiting(RateLimitPolicies.Redirect)
            .Produces(StatusCodes.Status302Found)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status410Gone)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        // Link checkers and preview bots probe with HEAD: same answer, but it is not a visit and spends no click.
        app.MapMethods("/{slug:slug}", [HttpMethods.Head], (string slug, HttpContext http, RedirectService redirects, CancellationToken cancellationToken) =>
                ResolveAsync(slug, http, redirects, recordClick: false, cancellationToken))
            .RequireRateLimiting(RateLimitPolicies.Redirect)
            .ExcludeFromDescription();
    }

    private static async Task<IResult> ResolveAsync(
        string slug,
        HttpContext http,
        RedirectService redirects,
        bool recordClick,
        CancellationToken cancellationToken)
    {
        http.Response.Headers.CacheControl = "no-store";

        var outcome = await redirects.ResolveAsync(slug, http, recordClick, cancellationToken);
        return outcome.Kind switch
        {
            RedirectKind.Found => TypedResults.Redirect(outcome.Url!),
            RedirectKind.Expired => Problems.LinkExpired(),
            RedirectKind.Exhausted => Problems.LinkExhausted(),
            _ => Problems.NotFound($"No link with slug '{slug}'."),
        };
    }
}
