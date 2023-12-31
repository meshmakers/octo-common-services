using Meshmakers.Octo.Services.Swagger;
using Meshmakers.Octo.Services.Swagger.Configuration;
using Microsoft.Extensions.Options;
using Swashbuckle.AspNetCore.SwaggerGen;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddOctoApiVersioningAndDocumentation(
        this IServiceCollection services,
        Action<OctoSwaggerOptions>? setupOctoSwaggerOptionsAction = null)
    {
        if (setupOctoSwaggerOptionsAction != null) services.Configure(setupOctoSwaggerOptionsAction);

        services.AddApiVersioning(options => options.ReportApiVersions = true);
        services.AddVersionedApiExplorer();

        services.AddTransient<IConfigureOptions<SwaggerGenOptions>, ConfigureSwaggerOptions>();
        services.AddSwaggerGen();


        return services;
    }
}