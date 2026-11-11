using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi.Models;

namespace Curto.Api.Infrastructure;

/// <summary>OpenAPI document settings: title, description and the <c>X-API-Key</c> security scheme.</summary>
internal static class OpenApiSetup
{
    private const string Description = """
        Self-hostable URL shortener with privacy-friendly live analytics.

        * Management endpoints under `/api` need the `X-API-Key` header.
        * `GET /{slug}` (redirect), `GET /qr/{slug}.svg`, `/healthz`, `/readyz`, `/docs` and this document are public.
        * Every error is an RFC 9457 `application/problem+json` document with a `correlationId` that matches the `X-Correlation-Id` response header.
        * Timestamps are ISO 8601 in UTC; JSON properties are camelCase.
        """;

    /// <summary>Registers the OpenAPI document generator.</summary>
    /// <param name="services">Service collection.</param>
    public static IServiceCollection AddCurtoOpenApi(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info = new OpenApiInfo
                {
                    Title = "Curto API",
                    Version = AppInfo.Version,
                    Description = Description,
                    License = new OpenApiLicense { Name = "MIT", Url = new Uri("https://opensource.org/licenses/MIT") },
                };

                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes[ApiKeyDefaults.Scheme] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.ApiKey,
                    In = ParameterLocation.Header,
                    Name = ApiKeyDefaults.HeaderName,
                    Description = "The key from CURTO_API_KEY. Header only: it is never accepted in the URL.",
                };

                return Task.CompletedTask;
            });

            options.AddOperationTransformer((operation, context, _) =>
            {
                if (context.Description.ActionDescriptor.EndpointMetadata.OfType<IAuthorizeData>().Any())
                {
                    operation.Security =
                    [
                        new OpenApiSecurityRequirement
                        {
                            [new OpenApiSecurityScheme
                            {
                                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = ApiKeyDefaults.Scheme },
                            }] = [],
                        },
                    ];
                }

                return Task.CompletedTask;
            });
        });
    }
}
