using Meshmakers.Octo.Services.Swagger.Configuration;
using Microsoft.Extensions.Options;

namespace SampleWebService.Configuration;

// ReSharper disable once ClassNeverInstantiated.Global
internal class ConfigureOctoOpenApiOptions : IConfigureNamedOptions<OctoOpenApiOptions>
{

    public void Configure(OctoOpenApiOptions options)
    {
        Configure(Options.DefaultName, options);
    }

    public void Configure(string? name, OctoOpenApiOptions options)
    {
        options.AuthorityUrl = "https://localhost:5001/";
        options.ApiTitle = "Sample Web Service";
        options.ApiDescription = "Sample Web Service API";
        options.ClientId = "sample-web-service";
    }
}