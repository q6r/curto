using Curto.Api.Infrastructure;
using Curto.Api.Services;

namespace Curto.Api.Endpoints;

/// <summary>Public QR codes: <c>GET /qr/{slug}.svg</c>.</summary>
internal static class QrEndpoints
{
    /// <summary>Maps the QR endpoint.</summary>
    /// <param name="app">Route builder.</param>
    public static void MapQrEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/qr/{slug:slug}.svg", RenderAsync)
            .WithTags("QR")
            .WithName("QrCode")
            .WithSummary("QR code of a short URL (SVG)")
            .WithDescription("Optional query parameters: size (64-2048 pixels, default 256), dark and light (hex colours such as 1a2b3c or #fff).")
            .RequireRateLimiting(RateLimitPolicies.Redirect)
            .Produces<string>(StatusCodes.Status200OK, "image/svg+xml")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> RenderAsync(
        string slug,
        string? size,
        string? dark,
        string? light,
        HttpContext http,
        LinkService links,
        LinkMapper mapper,
        CancellationToken cancellationToken)
    {
        Dictionary<string, string[]> errors = [];

        var pixels = QrCodeRenderer.DefaultSize;
        if (size is not null
            && (!int.TryParse(size, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out pixels)
                || pixels is < QrCodeRenderer.MinSize or > QrCodeRenderer.MaxSize))
        {
            errors["size"] = [$"size must be a whole number between {QrCodeRenderer.MinSize} and {QrCodeRenderer.MaxSize}."];
        }

        var darkHex = QrCodeRenderer.DefaultDark;
        if (dark is not null && !QrCodeRenderer.TryParseHexColor(dark, out darkHex))
        {
            errors["dark"] = ["dark must be a hex colour such as 1a2b3c, #1a2b3c or #fff."];
        }

        var lightHex = QrCodeRenderer.DefaultLight;
        if (light is not null && !QrCodeRenderer.TryParseHexColor(light, out lightHex))
        {
            errors["light"] = ["light must be a hex colour such as ffffff, #ffffff or #fff."];
        }

        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        if (await links.GetAsync(slug, cancellationToken) is null)
        {
            return Problems.NotFound($"No link with slug '{slug}'.");
        }

        var svg = QrCodeRenderer.RenderSvg(mapper.ShortUrl(http.Request, slug), pixels, darkHex, lightHex);

        // The payload embeds the origin. Without a configured base URL that origin comes from the Host header,
        // so the response must not be shared between clients by a cache.
        http.Response.Headers.CacheControl = mapper.HasConfiguredBaseUrl ? "public, max-age=3600" : "no-store";
        return TypedResults.Text(svg, "image/svg+xml; charset=utf-8");
    }
}
