using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Curto.Api.Tests.Support;

/// <summary>Small helpers that keep the tests focused on behaviour instead of plumbing.</summary>
public static class HttpExtensions
{
    /// <summary>Reads a response body as a JSON document.</summary>
    /// <param name="response">Response to read.</param>
    public static async Task<JsonElement> JsonAsync(this HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Required string member.</summary>
    /// <param name="element">JSON object.</param>
    /// <param name="name">Member name.</param>
    public static string Str(this JsonElement element, string name) => element.GetProperty(name).GetString()!;

    /// <summary>Required integer member.</summary>
    /// <param name="element">JSON object.</param>
    /// <param name="name">Member name.</param>
    public static long Num(this JsonElement element, string name) => element.GetProperty(name).GetInt64();

    /// <summary>True when the member exists and is JSON null.</summary>
    /// <param name="element">JSON object.</param>
    /// <param name="name">Member name.</param>
    public static bool IsNull(this JsonElement element, string name) => element.GetProperty(name).ValueKind == JsonValueKind.Null;

    /// <summary>Names of all members of a JSON object, in document order.</summary>
    /// <param name="element">JSON object.</param>
    public static string[] Names(this JsonElement element) => [.. element.EnumerateObject().Select(p => p.Name)];

    /// <summary>String items of a JSON array member.</summary>
    /// <param name="element">JSON object.</param>
    /// <param name="name">Member name of the array.</param>
    public static string[] Strings(this JsonElement element, string name) =>
        [.. element.GetProperty(name).EnumerateArray().Select(item => item.GetString()!)];

    /// <summary>Sends a raw JSON body with the given method and media type.</summary>
    /// <param name="client">Client to use.</param>
    /// <param name="method">HTTP method.</param>
    /// <param name="uri">Request URI.</param>
    /// <param name="json">Request body.</param>
    /// <param name="mediaType">Content-Type media type.</param>
    public static async Task<HttpResponseMessage> SendJsonAsync(this HttpClient client, HttpMethod method, string uri, string json, string mediaType = "application/json")
    {
        ArgumentNullException.ThrowIfNull(client);

        using var request = new HttpRequestMessage(method, uri) { Content = new StringContent(json, Encoding.UTF8, mediaType) };
        return await client.SendAsync(request);
    }

    /// <summary>Asserts that a response is an RFC 9457 problem document with the standard members and returns it.</summary>
    /// <param name="response">Response to check.</param>
    /// <param name="status">Expected HTTP status.</param>
    public static async Task<JsonElement> ExpectProblemAsync(this HttpResponseMessage response, int status)
    {
        ArgumentNullException.ThrowIfNull(response);

        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await response.JsonAsync();
        Assert.Equal(status, problem.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrEmpty(problem.Str("title")));
        Assert.StartsWith("http", problem.Str("type"), StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(problem.Str("traceId")));

        // The id in the body is the one the server also returned in the header.
        Assert.Equal(response.Headers.GetValues("X-Correlation-Id").Single(), problem.Str("correlationId"));
        return problem;
    }
}
