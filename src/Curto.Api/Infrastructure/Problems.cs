using System.Diagnostics;
using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Curto.Api.Infrastructure;

/// <summary>Factories for RFC 9457 problem responses, so every error leaves the service in the same shape.</summary>
internal static class Problems
{
    /// <summary>Response header carrying the correlation identifier.</summary>
    public const string CorrelationHeader = "X-Correlation-Id";

    /// <summary>400 with a field-keyed <c>errors</c> member.</summary>
    /// <param name="errors">Messages per field name.</param>
    public static IResult Validation(IReadOnlyDictionary<string, string[]> errors) =>
        new ProblemResult(new HttpValidationProblemDetails(new Dictionary<string, string[]>(errors, StringComparer.Ordinal))
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "One or more validation errors occurred.",
            Detail = "See the errors member for details.",
        });

    /// <summary>400 for a single field.</summary>
    /// <param name="field">Field name as used in the JSON contract.</param>
    /// <param name="message">What is wrong.</param>
    public static IResult Validation(string field, string message) =>
        Validation(new Dictionary<string, string[]> { [field] = [message] });

    /// <summary>404.</summary>
    /// <param name="detail">Human-readable explanation.</param>
    public static IResult NotFound(string detail) => Create(StatusCodes.Status404NotFound, detail);

    /// <summary>409 for a slug that is already in use.</summary>
    /// <param name="slug">The contested slug.</param>
    public static IResult SlugTaken(string slug) =>
        Create(StatusCodes.Status409Conflict, $"The slug '{slug}' is already in use.", "slug_taken");

    /// <summary>410 for a link past its expiry.</summary>
    public static IResult LinkExpired() =>
        Create(StatusCodes.Status410Gone, "This link has expired.", "link_expired");

    /// <summary>410 for a link whose click budget is spent.</summary>
    public static IResult LinkExhausted() =>
        Create(StatusCodes.Status410Gone, "This link has reached its click limit.", "link_exhausted");

    /// <summary>401 asking for the API key.</summary>
    public static IResult Unauthorized() =>
        Create(StatusCodes.Status401Unauthorized, "A valid X-API-Key header is required.");

    /// <summary>429, optionally with a <c>Retry-After</c> hint.</summary>
    /// <param name="retryAfter">When the client may try again.</param>
    public static IResult TooManyRequests(TimeSpan? retryAfter)
    {
        var detail = "Rate limit exceeded.";
        Dictionary<string, string>? headers = null;
        if (retryAfter is { } wait)
        {
            var seconds = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
            detail = string.Create(CultureInfo.InvariantCulture, $"Rate limit exceeded. Retry after {seconds} seconds.");
            headers = new() { ["Retry-After"] = seconds.ToString(CultureInfo.InvariantCulture) };
        }

        return new ProblemResult(
            new ProblemDetails { Status = StatusCodes.Status429TooManyRequests, Detail = detail },
            headers);
    }

    /// <summary>503 with an optional <c>Retry-After</c> hint.</summary>
    /// <param name="detail">Human-readable explanation.</param>
    /// <param name="retryAfterSeconds">Seconds the client should wait.</param>
    public static IResult ServiceUnavailable(string detail, int retryAfterSeconds = 1) =>
        new ProblemResult(
            new ProblemDetails { Status = StatusCodes.Status503ServiceUnavailable, Detail = detail },
            new Dictionary<string, string> { ["Retry-After"] = retryAfterSeconds.ToString(CultureInfo.InvariantCulture) });

    /// <summary>A problem for any status, with the standard title and type for that status.</summary>
    /// <param name="status">HTTP status code.</param>
    /// <param name="detail">Human-readable explanation.</param>
    /// <param name="code">Optional machine-readable discriminator for clients.</param>
    public static IResult Create(int status, string? detail = null, string? code = null)
    {
        var problem = new ProblemDetails { Status = status, Detail = detail };
        if (code is not null)
        {
            problem.Extensions["code"] = code;
        }

        return new ProblemResult(problem);
    }

    /// <summary>Adds the members every problem document carries: type, title, instance, trace and correlation ids.</summary>
    /// <param name="http">Current request.</param>
    /// <param name="problem">Document to complete; existing values are kept.</param>
    public static void Enrich(HttpContext http, ProblemDetails problem)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(problem);

        var status = problem.Status ?? StatusCodes.Status500InternalServerError;
        problem.Status = status;
        problem.Type ??= StatusTypes.TypeFor(status);
        problem.Title ??= StatusTypes.TitleFor(status);
        problem.Detail ??= StatusTypes.DefaultDetailFor(status);
        problem.Instance ??= http.Request.Path.Value;
        problem.Extensions["traceId"] = Activity.Current?.Id ?? http.TraceIdentifier;
        problem.Extensions["correlationId"] = http.TraceIdentifier;
    }

    /// <summary>Completes the problem document and writes it as <c>application/problem+json</c>.</summary>
    /// <param name="http">Current request.</param>
    /// <param name="problem">Document to write.</param>
    /// <param name="headers">Extra response headers (for example <c>Retry-After</c>).</param>
    public static Task WriteAsync(HttpContext http, ProblemDetails problem, IReadOnlyDictionary<string, string>? headers = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(problem);

        Enrich(http, problem);
        http.Response.StatusCode = problem.Status!.Value;
        if (headers is not null)
        {
            foreach (var (name, value) in headers)
            {
                http.Response.Headers[name] = value;
            }
        }

        var json = http.RequestServices.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
        return http.Response.WriteAsJsonAsync(problem, problem.GetType(), json, "application/problem+json", http.RequestAborted);
    }

    /// <summary>Result that writes a problem document when the endpoint returns it.</summary>
    private sealed class ProblemResult(ProblemDetails problem, IReadOnlyDictionary<string, string>? headers = null) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext) => WriteAsync(httpContext, problem, headers);
    }
}

/// <summary>Standard titles and RFC 9110 type URIs for the statuses this service produces.</summary>
internal static class StatusTypes
{
    /// <summary>Type URI for a status code.</summary>
    /// <param name="status">HTTP status code.</param>
    public static string TypeFor(int status) => status switch
    {
        400 => "https://tools.ietf.org/html/rfc9110#section-15.5.1",
        401 => "https://tools.ietf.org/html/rfc9110#section-15.5.2",
        404 => "https://tools.ietf.org/html/rfc9110#section-15.5.5",
        405 => "https://tools.ietf.org/html/rfc9110#section-15.5.6",
        409 => "https://tools.ietf.org/html/rfc9110#section-15.5.10",
        410 => "https://tools.ietf.org/html/rfc9110#section-15.5.11",
        413 => "https://tools.ietf.org/html/rfc9110#section-15.5.14",
        415 => "https://tools.ietf.org/html/rfc9110#section-15.5.16",
        429 => "https://tools.ietf.org/html/rfc6585#section-4",
        503 => "https://tools.ietf.org/html/rfc9110#section-15.6.4",
        >= 500 => "https://tools.ietf.org/html/rfc9110#section-15.6.1",
        _ => "about:blank",
    };

    /// <summary>
    /// Explanation for statuses the framework produces on its own (routing, content negotiation), where no endpoint
    /// code had the chance to say more. Null when the status needs no extra words.
    /// </summary>
    /// <param name="status">HTTP status code.</param>
    public static string? DefaultDetailFor(int status) => status switch
    {
        404 => "No resource matches this path.",
        405 => "This method is not allowed for this resource.",
        415 => "The request body must be JSON (Content-Type: application/json).",
        _ => null,
    };

    /// <summary>Reason-phrase style title for a status code.</summary>
    /// <param name="status">HTTP status code.</param>
    public static string TitleFor(int status) => status switch
    {
        400 => "Bad Request",
        401 => "Unauthorized",
        404 => "Not Found",
        405 => "Method Not Allowed",
        409 => "Conflict",
        410 => "Gone",
        413 => "Content Too Large",
        415 => "Unsupported Media Type",
        429 => "Too Many Requests",
        503 => "Service Unavailable",
        >= 500 => "Internal Server Error",
        _ => "Error",
    };
}
