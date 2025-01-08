using Meshmakers.Octo.Services.Swagger.Transformers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Services.Swagger.Configuration.DependencyInjection;

internal class OpenApiBuilder(IServiceCollection serviceCollection) : IOpenApiBuilder
{
    public IServiceCollection Services { get; } = serviceCollection;
    
    public IOpenApiBuilder AddVersion(string versionName = "v1")
    {
        Services.AddOpenApi(versionName, options =>
        {
            options.AddDocumentTransformer((document, ctx, _) =>
            {
                var octoOptions = ctx.ApplicationServices.GetRequiredService<IOptions<OctoOpenApiOptions>>();

                document.Info.Title = octoOptions.Value.ApiTitle;
                document.Info.Description = octoOptions.Value.ApiDescription;
                document.Info.Version = versionName;
                document.Info.Contact = new Microsoft.OpenApi.Models.OpenApiContact
                    { Name = OpenApiConstants.CompanyName, Email = OpenApiConstants.MailAddress };

                return Task.CompletedTask;
            });

            options.AddDocumentTransformer<SecuritySchemeTransformer>();
        });

        return this;
    }
}