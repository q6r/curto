namespace Curto.Api.Infrastructure;

/// <summary>
/// Gives every request a correlation id: the caller's <c>X-Correlation-Id</c> when it is a short, harmless token,
/// a fresh time-ordered GUID otherwise. The id is echoed in the response, used as the request's trace identifier
/// and attached to the log scope.
/// </summary>
/// <param name="next">Next middleware.</param>
/// <param name="logger">Used to open the log scope.</param>
internal sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    private const int MaxLength = 64;

    /// <summary>Runs the middleware.</summary>
    /// <param name="context">Current request.</param>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var supplied = context.Request.Headers[Problems.CorrelationHeader].ToString();
        var id = IsAcceptable(supplied) ? supplied : Guid.CreateVersion7().ToString("N");

        context.TraceIdentifier = id;
        context.Response.OnStarting(
            static state =>
            {
                var (response, value) = ((HttpResponse, string))state;
                response.Headers[Problems.CorrelationHeader] = value;
                return Task.CompletedTask;
            },
            (context.Response, id));

        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = id }))
        {
            await next(context);
        }
    }

    /// <summary>True for 1-64 characters from <c>[A-Za-z0-9._-]</c>, which can never smuggle control characters into logs or headers.</summary>
    /// <param name="candidate">Header value from the client.</param>
    internal static bool IsAcceptable(string candidate)
    {
        if (candidate.Length is 0 or > MaxLength)
        {
            return false;
        }

        foreach (var c in candidate)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }
}
