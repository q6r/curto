using System.Security.Cryptography;

namespace Curto.Api.Infrastructure;

/// <summary>
/// Adds the browser hardening headers to every response. The default policy allows resources from this origin only;
/// the interactive API reference under <c>/docs</c> gets a narrow exception because the Scalar bundle injects styles.
/// </summary>
/// <param name="next">Next middleware.</param>
internal sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    /// <summary><see cref="HttpContext.Items"/> key holding the per-request script nonce on documentation pages.</summary>
    public const string NonceItemKey = "curto.csp-nonce";

    /// <summary>Policy for the dashboard, the API and every error document.</summary>
    public const string DefaultPolicy =
        "default-src 'self'; img-src 'self' data:; script-src 'self'; style-src 'self'; " +
        "object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";

    // Scalar injects <style> elements at run time, which only 'unsafe-inline' can allow; scripts stay nonce-protected.
    private const string DocsPolicyHead =
        "default-src 'self'; img-src 'self' data:; font-src 'self' data:; connect-src 'self'; script-src 'self' 'nonce-";

    private const string DocsPolicyTail =
        "'; style-src 'self' 'unsafe-inline'; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";

    /// <summary>Runs the middleware.</summary>
    /// <param name="context">Current request.</param>
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        string policy = DefaultPolicy;
        if (context.Request.Path.StartsWithSegments("/docs", StringComparison.OrdinalIgnoreCase))
        {
            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            context.Items[NonceItemKey] = nonce;
            policy = string.Concat(DocsPolicyHead, nonce, DocsPolicyTail);
        }

        var isApi = context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase);
        context.Response.OnStarting(
            static state =>
            {
                var (http, csp, noStore) = ((HttpContext, string, bool))state;
                var headers = http.Response.Headers;
                headers.ContentSecurityPolicy = csp;
                headers.XContentTypeOptions = "nosniff";
                headers.XFrameOptions = "DENY";
                headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
                headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
                if (http.Request.IsHttps)
                {
                    headers.StrictTransportSecurity = "max-age=31536000";
                }

                if (noStore && !headers.ContainsKey("Cache-Control"))
                {
                    headers.CacheControl = "no-store";
                }

                return Task.CompletedTask;
            },
            (context, policy, isApi));

        return next(context);
    }
}
