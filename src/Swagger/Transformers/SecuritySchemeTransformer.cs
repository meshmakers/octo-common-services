using Meshmakers.Common.Shared;
using Meshmakers.Octo.Services.Swagger.Configuration;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;

namespace Meshmakers.Octo.Services.Swagger.Transformers;

internal sealed class SecuritySchemeTransformer(IOptions<OctoOpenApiOptions> options)
    : IOpenApiDocumentTransformer
{
    private const string SchemeId = "Bearer";

    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Value.AuthorityUrl))
        {
            throw new InvalidOperationException("AuthorityUrl must be set in the OctoSwaggerOptions");
        }

        var schemes = new Dictionary<string, OpenApiSecurityScheme>
        {
            [SchemeId] = new()
            {
                Type = SecuritySchemeType.OAuth2,
                Flows = new OpenApiOAuthFlows
                {
                    AuthorizationCode = new OpenApiOAuthFlow
                    {
                        AuthorizationUrl =
                            new Uri($"{options.Value.AuthorityUrl.EnsureEndsWith("/")}connect/authorize"),
                        TokenUrl = new Uri($"{options.Value.AuthorityUrl.EnsureEndsWith("/")}connect/token"),
                        Scopes = options.Value.Scopes
                    }
                }
            }
        };

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes = schemes;

        foreach (var operation in document.Paths.Values.SelectMany(path => path.Operations))
        {
            if (operation.Value.Responses.Any(r => r.Key == "401"))
            {
                operation.Value.Security.Add(new OpenApiSecurityRequirement
                {
                    {
                        new OpenApiSecurityScheme
                        {
                            Reference = new OpenApiReference
                            {
                                Id = SchemeId,
                                Type = ReferenceType.SecurityScheme
                            }
                        },
                        Array.Empty<string>()
                    }
                });
            }
        }

        return Task.CompletedTask;
    }
}