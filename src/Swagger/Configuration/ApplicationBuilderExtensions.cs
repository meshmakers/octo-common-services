using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Services.Swagger.Configuration;

public static class ApplicationBuilderExtensions
{
    /// <summary>
    ///     Adds Octo to the pipeline.
    /// </summary>
    /// <param name="app">The application.</param>
    /// <returns></returns>
    // ReSharper disable once UnusedMethodReturnValue.Global
    public static IApplicationBuilder UseOctoApiVersioningAndDocumentation(
        this IApplicationBuilder app)
    {
        // Enable middleware to serve generated Swagger as a JSON endpoint.
        app.UseSwagger();

        // Enable middleware to serve swagger-ui (HTML, JS, CSS, etc.), 
        // specifying the Swagger JSON endpoint.
        var provider = app.ApplicationServices.GetRequiredService<IApiVersionDescriptionProvider>();
        var octoOptions = app.ApplicationServices.GetRequiredService<IOptions<OctoSwaggerOptions>>();
        app.UseSwaggerUI(
            options =>
            {
                // build a swagger endpoint for each discovered API version
                foreach (var description in provider.ApiVersionDescriptions)
                {
                    options.SwaggerEndpoint($"/swagger/{description.GroupName}/swagger.json",
                        description.GroupName.ToUpperInvariant());
                }

                options.InjectStylesheet("/css/swagger.css");

                options.OAuthClientId(octoOptions.Value.ClientId);
                options.OAuthAppName(octoOptions.Value.AppName);
                options.OAuthUsePkce();
            });

        return app;
    }
}