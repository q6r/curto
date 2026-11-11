namespace Curto.Api.Infrastructure;

/// <summary>
/// Fallback problem writer. The framework's default writer declines when the client's <c>Accept</c> header does not
/// admit JSON; errors from this service are always <c>application/problem+json</c>, whatever the client asked for.
/// </summary>
internal sealed class JsonProblemWriter : IProblemDetailsWriter
{
    /// <inheritdoc />
    public bool CanWrite(ProblemDetailsContext context) => true;

    /// <inheritdoc />
    public ValueTask WriteAsync(ProblemDetailsContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return new ValueTask(Problems.WriteAsync(context.HttpContext, context.ProblemDetails));
    }
}
