using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Curto.Api.Infrastructure;

/// <summary>Names used by the API-key scheme and its authorization policy.</summary>
internal static class ApiKeyDefaults
{
    /// <summary>Authentication scheme name.</summary>
    public const string Scheme = "ApiKey";

    /// <summary>Authorization policy that management endpoints require.</summary>
    public const string Policy = "ApiKey";

    /// <summary>Request header carrying the key. Never a query parameter: URLs end up in logs and referrers.</summary>
    public const string HeaderName = "X-API-Key";
}

/// <summary>
/// Holds the effective API key (configured, or generated in Development) and compares candidates in constant time.
/// Both sides are hashed first so the comparison does not leak the key's length either.
/// </summary>
internal sealed class ApiKeyVerifier
{
    private const string Base62 = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    private readonly byte[] _expectedDigest;

    /// <summary>Creates the verifier from configuration.</summary>
    /// <param name="options">Configured key, if any.</param>
    /// <param name="environment">Allows key generation in Development only.</param>
    public ApiKeyVerifier(IOptions<CurtoOptions> options, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        var key = options.Value.ApiKey;
        if (string.IsNullOrEmpty(key))
        {
            if (!environment.IsDevelopment())
            {
                throw new InvalidOperationException("No API key configured. Set CURTO_API_KEY.");
            }

            key = "dev_" + RandomNumberGenerator.GetString(Base62, 32);
            GeneratedKey = key;
        }

        _expectedDigest = SHA256.HashData(Encoding.UTF8.GetBytes(key));
    }

    /// <summary>The key generated for this run, or null when one was configured. Printed once at start-up.</summary>
    public string? GeneratedKey { get; }

    /// <summary>Checks a candidate key.</summary>
    /// <param name="candidate">Header value.</param>
    public bool IsValid(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate))
        {
            return false;
        }

        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(candidate), digest);
        return CryptographicOperations.FixedTimeEquals(digest, _expectedDigest);
    }
}

/// <summary>Authenticates a request by its <c>X-API-Key</c> header and challenges with a problem document.</summary>
internal sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ApiKeyVerifier verifier) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    /// <inheritdoc />
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiKeyDefaults.HeaderName, out var values) || values.Count != 1)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!verifier.IsValid(values[0]))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
        }

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "api-key")], Scheme.Name);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    /// <inheritdoc />
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = "ApiKey realm=\"curto\"";
        return Problems.Unauthorized().ExecuteAsync(Context);
    }
}
