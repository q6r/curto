using Curto.Api.Infrastructure;
using Curto.Api.Services;
using Curto.Api.Storage;

namespace Curto.Api.Endpoints;

/// <summary>Liveness and readiness probes.</summary>
internal static class HealthEndpoints
{
    /// <summary>Liveness answer.</summary>
    /// <param name="Status">Always <c>ok</c>.</param>
    /// <param name="Version">Build version.</param>
    internal sealed record Liveness(string Status, string Version);

    /// <summary>Readiness answer.</summary>
    /// <param name="Status"><c>ready</c>.</param>
    /// <param name="Version">Build version.</param>
    /// <param name="Database"><c>ok</c> when the database answered.</param>
    /// <param name="ClickQueue">Counters of the click pipeline, including how many events were dropped on overflow.</param>
    /// <param name="LiveSubscribers">Connected live-stream clients.</param>
    /// <param name="SlowConsumersDropped">Live-stream clients disconnected for being too slow.</param>
    internal sealed record Readiness(
        string Status,
        string Version,
        string Database,
        ClickQueueStats ClickQueue,
        int LiveSubscribers,
        long SlowConsumersDropped);

    /// <summary>Maps <c>/healthz</c> and <c>/readyz</c>.</summary>
    /// <param name="app">Route builder.</param>
    public static void MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/healthz", () => TypedResults.Ok(new Liveness("ok", AppInfo.Version)))
            .WithTags("Health")
            .WithName("Liveness")
            .WithSummary("Liveness probe: the process is up")
            .Produces<Liveness>();

        app.MapGet("/readyz", ReadyAsync)
            .WithTags("Health")
            .WithName("Readiness")
            .WithSummary("Readiness probe: the database answers; reports click-pipeline counters")
            .Produces<Readiness>()
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<IResult> ReadyAsync(
        HttpContext http,
        Database database,
        ClickChannel clicks,
        EventBroadcaster broadcaster,
        CancellationToken cancellationToken)
    {
        http.Response.Headers.CacheControl = "no-store";

        if (!await database.PingAsync(cancellationToken))
        {
            return Problems.ServiceUnavailable("The database is not reachable.", retryAfterSeconds: 5);
        }

        return TypedResults.Ok(new Readiness(
            "ready",
            AppInfo.Version,
            "ok",
            clicks.Snapshot(),
            broadcaster.SubscriberCount,
            broadcaster.SlowConsumersDropped));
    }
}
