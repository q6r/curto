using Curto.Api.Infrastructure;
using Curto.Api.Models;
using Curto.Api.Services;

namespace Curto.Api.Endpoints;

/// <summary>Analytics endpoints: <c>/api/stats/overview</c> and <c>/api/links/{slug}/stats</c>.</summary>
internal static class StatsEndpoints
{
    private const string RangeError = "range must be one of: 24h, 7d, 30d, 90d.";

    /// <summary>Maps the statistics endpoints below the (already authorised) API group.</summary>
    /// <param name="api">The <c>/api</c> group.</param>
    public static void MapStatsEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        api.MapGet("/stats/overview", OverviewAsync)
            .WithTags("Stats")
            .WithName("StatsOverview")
            .WithSummary("Analytics across all links")
            .WithDescription("Windows are aligned to whole UTC buckets: 24h uses 24 hourly buckets, 7d/30d/90d use daily buckets, and the last bucket contains the current time.")
            .Produces<StatsResponse>()
            .ProducesValidationProblem();

        api.MapGet("/links/{slug:slug}/stats", LinkStatsAsync)
            .WithTags("Stats")
            .WithName("LinkStats")
            .WithSummary("Analytics of one link")
            .WithDescription("Same shape as the overview; links.total is 1 and topLinks contains only this link.")
            .Produces<StatsResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> OverviewAsync(string? range, StatsService stats, CancellationToken cancellationToken) =>
        StatsService.TryParseWindow(range, out var window)
            ? TypedResults.Ok(await stats.OverviewAsync(window, cancellationToken))
            : Problems.Validation("range", RangeError);

    private static async Task<IResult> LinkStatsAsync(string slug, string? range, StatsService stats, CancellationToken cancellationToken)
    {
        if (!StatsService.TryParseWindow(range, out var window))
        {
            return Problems.Validation("range", RangeError);
        }

        var response = await stats.ForLinkAsync(slug, window, cancellationToken);
        return response is null
            ? Problems.NotFound($"No link with slug '{slug}'.")
            : TypedResults.Ok(response);
    }
}
