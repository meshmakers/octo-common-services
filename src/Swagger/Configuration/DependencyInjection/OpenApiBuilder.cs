using Meshmakers.Octo.Services.Swagger.Transformers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;

namespace Meshmakers.Octo.Services.Swagger.Configuration.DependencyInjection;

internal class OpenApiBuilder(IServiceCollection serviceCollection) : IOpenApiBuilder
{
    public IServiceCollection Services { get; } = serviceCollection;
    
    public IOpenApiBuilder AddVersion(string versionName = OpenApiConstants.Version1)
    {
        Services.AddOpenApi(versionName, options =>
        {
            options.AddOperationTransformer<AuthenticationOperationTransformer>();

            options.AddDocumentTransformer((document, ctx, _) =>
            {
                var octoOptions = ctx.ApplicationServices.GetRequiredService<IOptions<OctoOpenApiOptions>>();

                document.Info.Title = octoOptions.Value.ApiTitle;
                document.Info.Description = octoOptions.Value.ApiDescription;
                document.Info.Version = versionName;
                document.Info.Contact = new OpenApiContact
                    { Name = OpenApiConstants.CompanyName, Email = OpenApiConstants.MailAddress };

                return Task.CompletedTask;
            });

            options.AddDocumentTransformer<SecuritySchemeTransformer>();
            options.AddDocumentTransformer<XmlDocOperationTransformer>();
            options.AddSchemaTransformer<XmlDocSchemaTransformer>();

            // We ignore all operations that do not have an HTTP method, as they are not relevant for the API
            // This situation occurs at reporting services, because Telerik provides obsolete methods that have no HTTP method
            options.ShouldInclude = operation => operation.HttpMethod != null;
        });

        return this;
    }
}