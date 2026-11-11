namespace Curto.Api.Infrastructure;

/// <summary>
/// <c>Curto.Api --healthcheck</c>: asks the locally running server for <c>/healthz</c> and exits 0 or 1.
/// The runtime container image ships neither curl nor wget, so the Docker HEALTHCHECK uses the application itself.
/// </summary>
internal static class HealthProbe
{
    /// <summary>Command-line flag that selects probe mode.</summary>
    public const string Flag = "--healthcheck";

    private const int DefaultPort = 8080;

    /// <summary>Works out where the local server listens, from the standard ASP.NET Core variables.</summary>
    /// <param name="getVariable">Environment lookup.</param>
    public static Uri ResolveUrl(Func<string, string?> getVariable)
    {
        ArgumentNullException.ThrowIfNull(getVariable);

        var port = DefaultPort;

        if (FirstHttpUrlPort(getVariable("ASPNETCORE_URLS")) is { } fromUrls)
        {
            port = fromUrls;
        }
        else if (getVariable("ASPNETCORE_HTTP_PORTS") is { Length: > 0 } ports
            && int.TryParse(ports.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0], out var fromPorts))
        {
            port = fromPorts;
        }

        return new Uri($"http://127.0.0.1:{port}/healthz");
    }

    /// <summary>Calls the endpoint once.</summary>
    /// <param name="url">Health URL.</param>
    /// <param name="client">HTTP client (injectable for tests).</param>
    /// <param name="cancellationToken">Cancels the probe.</param>
    /// <returns>0 when the server answered 2xx, 1 otherwise.</returns>
    public static async Task<int> RunAsync(Uri url, HttpClient client, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(client);

        try
        {
            using var response = await client.GetAsync(url, cancellationToken);
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (HttpRequestException)
        {
            return 1;
        }
        catch (TaskCanceledException)
        {
            return 1;
        }
    }

    private static int? FirstHttpUrlPort(string? urls)
    {
        if (string.IsNullOrWhiteSpace(urls))
        {
            return null;
        }

        foreach (var entry in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Wildcard hosts ("+", "*") are not valid in a Uri, so parse the port by hand.
            if (!entry.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var colon = entry.LastIndexOf(':');
            if (colon > "http://".Length - 1 && int.TryParse(entry[(colon + 1)..].TrimEnd('/'), out var port))
            {
                return port;
            }
        }

        return null;
    }
}
