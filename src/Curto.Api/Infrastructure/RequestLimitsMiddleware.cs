using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Curto.Api.Infrastructure;

/// <summary>
/// Bounds request bodies. A declared <c>Content-Length</c> above the limit is refused before any parsing;
/// streams of unknown length are capped through the server's body-size feature when it exists (Kestrel).
/// </summary>
/// <param name="next">Next middleware.</param>
/// <param name="options">Source of the byte limit.</param>
internal sealed class RequestLimitsMiddleware(RequestDelegate next, IOptions<CurtoOptions> options)
{
    private readonly long _limit = options.Value.MaxRequestBodyBytes;

    /// <summary>Runs the middleware.</summary>
    /// <param name="context">Current request.</param>
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Request.ContentLength > _limit)
        {
            return Problems.WriteAsync(
                context,
                new ProblemDetails
                {
                    Status = StatusCodes.Status413PayloadTooLarge,
                    Detail = "The request body is too large.",
                });
        }

        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
        {
            feature.MaxRequestBodySize = _limit;
        }

        return next(context);
    }
}
