using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Data.Sqlite;

namespace Curto.Api.Infrastructure;

/// <summary>
/// Turns every unhandled exception into a problem document. Framework request errors keep their status code,
/// a busy database becomes 503, everything else is logged and answered with a bare 500 (no internals leak).
/// </summary>
/// <param name="logger">Receives unexpected failures.</param>
internal sealed partial class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;

    /// <inheritdoc />
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            LogClientAborted(logger, httpContext.Request.Path);
            return true;
        }

        IResult result;
        switch (exception)
        {
            case BadHttpRequestException bad:
                result = Problems.Create(bad.StatusCode, DetailFor(bad.StatusCode));
                break;

            case SqliteException { SqliteErrorCode: SqliteBusy or SqliteLocked } busy:
                LogDatabaseBusy(logger, busy);
                result = Problems.ServiceUnavailable("The database is busy. Retry shortly.");
                break;

            default:
                LogUnhandled(logger, exception, httpContext.Request.Method, httpContext.Request.Path);
                result = Problems.Create(StatusCodes.Status500InternalServerError, "An unexpected error occurred.");
                break;
        }

        await result.ExecuteAsync(httpContext);
        return true;
    }

    private static string DetailFor(int status) => status switch
    {
        StatusCodes.Status413PayloadTooLarge => "The request body is too large.",
        StatusCodes.Status415UnsupportedMediaType => "The request body must be JSON (Content-Type: application/json).",
        StatusCodes.Status408RequestTimeout => "The request took too long to arrive.",
        _ => "The request is malformed: check the JSON syntax, the field types and the parameter values.",
    };

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Unhandled exception while serving {Method} {Path}")]
    private static partial void LogUnhandled(ILogger logger, Exception exception, string method, PathString path);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "SQLite reported a busy database")]
    private static partial void LogDatabaseBusy(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "Client aborted {Path}")]
    private static partial void LogClientAborted(ILogger logger, PathString path);
}
