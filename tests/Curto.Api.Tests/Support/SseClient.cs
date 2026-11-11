namespace Curto.Api.Tests.Support;

/// <summary>One frame of a Server-Sent Events stream.</summary>
/// <param name="Event">Value of the <c>event:</c> field, if any.</param>
/// <param name="Data">Value of the <c>data:</c> field, if any.</param>
/// <param name="Comments">Comment lines (<c>: text</c>) in the frame.</param>
public sealed record SseFrameData(string? Event, string? Data, IReadOnlyList<string> Comments);

/// <summary>Minimal Server-Sent Events reader over a streaming HTTP response.</summary>
public sealed class SseClient : IDisposable
{
    private readonly HttpResponseMessage _response;
    private readonly StreamReader _reader;
    private readonly CancellationTokenSource _connection;

    private SseClient(HttpResponseMessage response, StreamReader reader, CancellationTokenSource connection)
    {
        _response = response;
        _reader = reader;
        _connection = connection;
    }

    /// <summary>The HTTP response (headers are available as soon as the stream opens).</summary>
    public HttpResponseMessage Response => _response;

    /// <summary>Opens the stream.</summary>
    /// <param name="client">HTTP client of the test host.</param>
    /// <param name="apiKey">Key to send in the header, or null for none.</param>
    public static async Task<SseClient> OpenAsync(HttpClient client, string? apiKey = CurtoFactory.ApiKey)
    {
        ArgumentNullException.ThrowIfNull(client);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/events");
        if (apiKey is not null)
        {
            request.Headers.Add("X-API-Key", apiKey);
        }

        // Cancelling this token is how the client "hangs up": the server sees RequestAborted fire.
        var connection = new CancellationTokenSource();
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, connection.Token);
        var stream = await response.Content.ReadAsStreamAsync(connection.Token);
        return new SseClient(response, new StreamReader(stream), connection);
    }

    /// <summary>Reads the next frame (a block of lines ended by a blank line).</summary>
    /// <param name="timeout">Longest wait; defaults to 10 seconds.</param>
    public async Task<SseFrameData> ReadFrameAsync(TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        string? name = null;
        string? data = null;
        List<string> comments = [];
        var sawLine = false;

        while (true)
        {
            var line = await _reader.ReadLineAsync(cts.Token);
            if (line is null)
            {
                Assert.Fail("The stream ended unexpectedly.");
            }

            if (line.Length == 0)
            {
                if (sawLine)
                {
                    return new SseFrameData(name, data, comments);
                }

                continue;
            }

            sawLine = true;
            if (line.StartsWith(':'))
            {
                comments.Add(line[1..].Trim());
            }
            else if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                name = line["event:".Length..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                data = line["data:".Length..].Trim();
            }
        }
    }

    /// <summary>Reads frames until one carries an event with the given name.</summary>
    /// <param name="eventName">Event name to wait for.</param>
    public async Task<SseFrameData> ReadEventAsync(string eventName)
    {
        while (true)
        {
            var frame = await ReadFrameAsync();
            if (frame.Event == eventName)
            {
                return frame;
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _connection.Cancel();
        _reader.Dispose();
        _response.Dispose();
        _connection.Dispose();
    }
}
