using System.Collections.Generic;
using System.Linq;
using Meshmakers.Octo.Common.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Meshmakers.Octo.Backend.Swagger.Swagger;

// ReSharper disable once ClassNeverInstantiated.Global
public class AuthorizeCheckOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var hasAuthorize =
            context.MethodInfo.DeclaringType != null && (context.MethodInfo.DeclaringType.GetCustomAttributes(true)
                                                             .OfType<AuthorizeAttribute>().Any()
                                                         || context.MethodInfo.GetCustomAttributes(true)
                                                             .OfType<AuthorizeAttribute>().Any());

        if (hasAuthorize)
        {
            operation.Responses.TryAdd("401", new OpenApiResponse { Description = "Unauthorized" });
            operation.Responses.TryAdd("403", new OpenApiResponse { Description = "Forbidden" });

            operation.Security = new List<OpenApiSecurityRequirement>
            {
                new()
                {
                    [
                        new OpenApiSecurityScheme
                        {
                            Reference = new OpenApiReference
                            {
                                Type = ReferenceType.SecurityScheme,
                                Id = "oauth2"
                            }
                        }
                    ] = new[] { CommonConstants.IdentityApi }
                }
            };
        }
    }
}
