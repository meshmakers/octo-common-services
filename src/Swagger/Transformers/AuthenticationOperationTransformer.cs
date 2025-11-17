using Meshmakers.Octo.Services.Swagger.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

namespace Meshmakers.Octo.Services.Swagger.Transformers;

internal class AuthenticationOperationTransformer(IOptions<OctoOpenApiOptions> options) : IOpenApiOperationTransformer
{
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (context.Description.ActionDescriptor.EndpointMetadata.FirstOrDefault(a => a is AuthorizeAttribute
            {
                Policy: not null
            }) is AuthorizeAttribute a)
        {
            if (a.Policy is null)
            {
                return Task.CompletedTask;
            }

            if (options.Value.PolicyScopeMapping.TryGetValue(a.Policy, out var scopes))
            {
                var r = new OpenApiSecuritySchemeReference("Bearer");
                var securityRequirement = new OpenApiSecurityRequirement
                {
                    {
                        r, scopes.ToList()
                    }
                };
                operation.Security = new List<OpenApiSecurityRequirement> { securityRequirement };
            }
        }
        return Task.CompletedTask;
    }
}