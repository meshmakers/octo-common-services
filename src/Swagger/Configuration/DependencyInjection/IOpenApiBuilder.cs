using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Services.Swagger.Configuration.DependencyInjection;

/// <summary>
/// Builder for OpenApi configuration
/// </summary>
public interface IOpenApiBuilder
{
    /// <summary>
    /// Returns the services used when configuring OpenApi
    /// </summary>
    IServiceCollection Services { get; }

    /// <summary>
    /// Adds a version to the OpenApi configuration
    /// </summary>
    /// <param name="versionName"></param>
    /// <returns></returns>
    IOpenApiBuilder AddVersion(string versionName = "v1");
}