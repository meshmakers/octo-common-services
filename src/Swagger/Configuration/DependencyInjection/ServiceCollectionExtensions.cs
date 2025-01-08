using Asp.Versioning;
using Meshmakers.Octo.Services.Swagger.Configuration;
using Meshmakers.Octo.Services.Swagger.Configuration.DependencyInjection;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for setting up Octo OpenAPI services in an <see cref="IServiceCollection" />.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds Octo API versioning and documentation to the specified <see cref="IServiceCollection" />.
    /// </summary>
    /// <param name="services"></param>
    /// <param name="setupOctoSwaggerOptionsAction"></param>
    /// <returns></returns>
    public static IOpenApiBuilder AddOctoApiVersioningAndDocumentation(
        this IServiceCollection services,
        Action<OctoOpenApiOptions>? setupOctoSwaggerOptionsAction = null)
    {
        if (setupOctoSwaggerOptionsAction != null)
        {
            services.Configure(setupOctoSwaggerOptionsAction);
        }

        services.AddEndpointsApiExplorer();
        services.AddApiVersioning(option =>
        {
            option.AssumeDefaultVersionWhenUnspecified =
                true; //This ensures if client doesn't specify an API version. The default version should be considered. 
            option.DefaultApiVersion = new ApiVersion(1); //This we set the default API version
            option.ReportApiVersions =
                true; //The allow the API Version information to be reported in the client  in the response header. This will be useful for the client to understand the version of the API they are interacting with.
       
        }).AddApiExplorer(options =>
        {
            options.GroupNameFormat = "'v'VVV"; //The say our format of our version number “‘v’major[.minor][-status]”
            options.SubstituteApiVersionInUrl =
                true; //This will help us to resolve the ambiguity when there is a routing conflict due to routing template one or more end points are same.
        });


        return new OpenApiBuilder(services);
    }
}