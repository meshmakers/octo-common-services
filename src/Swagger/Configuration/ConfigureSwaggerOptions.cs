using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.Swagger.Swagger;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Meshmakers.Octo.Backend.Swagger.Configuration;

/// <summary>
///     Configures the Swagger generation options.
/// </summary>
/// <remarks>
///     This allows API versioning to define a Swagger document per API version after the
///     <see cref="IApiVersionDescriptionProvider" /> service has been resolved from the service container.
/// </remarks>
public class ConfigureSwaggerOptions : IConfigureOptions<SwaggerGenOptions>
{
    private readonly IOptions<OctoSwaggerOptions> _options;
    private readonly IApiVersionDescriptionProvider _provider;

    /// <summary>
    ///     Initializes a new instance of the <see cref="ConfigureSwaggerOptions" /> class.
    /// </summary>
    /// <param name="provider">
    ///     The <see cref="IApiVersionDescriptionProvider">provider</see> used to generate Swagger
    ///     documents.
    /// </param>
    /// <param name="options">Identity Services options</param>
    public ConfigureSwaggerOptions(IApiVersionDescriptionProvider provider, IOptions<OctoSwaggerOptions> options)
    {
        _provider = provider;
        _options = options;
    }

    /// <inheritdoc />
    public void Configure(SwaggerGenOptions options)
    {
        // add a swagger document for each discovered API version
        // note: you might choose to skip or document deprecated API versions differently
        foreach (var description in _provider.ApiVersionDescriptions)
        {
            options.SwaggerDoc(description.GroupName, CreateInfoForApiVersion(description));
        }

        foreach (var xmlPath in _options.Value.XmlDocAssemblies)
        {
            options.IncludeXmlComments(xmlPath);
        }

        options.AddSecurityRequirement(new OpenApiSecurityRequirement
        {
            {
                new OpenApiSecurityScheme
                {
                    Reference = new OpenApiReference
                    {
                        Id = "oauth2", Type = ReferenceType.SecurityScheme
                    }
                },
                new List<string>()
            }
        });

        if (_options.Value.AuthorityUrl != null)
            options.AddSecurityDefinition("oauth2", new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.OAuth2,
                Flows = new OpenApiOAuthFlows
                {
                    AuthorizationCode = new OpenApiOAuthFlow
                    {
                        AuthorizationUrl =
                            new Uri($"{_options.Value.AuthorityUrl.EnsureEndsWith("/")}connect/authorize"),
                        TokenUrl = new Uri($"{_options.Value.AuthorityUrl.EnsureEndsWith("/")}connect/token"),
                        Scopes = _options.Value.Scopes
                    }
                }
            });

        options.OperationFilter<AuthorizeCheckOperationFilter>();
    }


    private OpenApiInfo CreateInfoForApiVersion(ApiVersionDescription description)
    {
        var info = new OpenApiInfo
        {
            Title = _options.Value.ApiTitle,
            Version = description.ApiVersion.ToString(),
            Description = _options.Value.ApiDescription,
            Contact = new OpenApiContact { Name = "Gerald Lochner", Email = "gerald.lochner@salzburgdev.at" },
            License = new OpenApiLicense { Name = "MIT", Url = new Uri("https://opensource.org/licenses/MIT") }
        };

        if (description.IsDeprecated) info.Description += " This API version has been deprecated.";

        return info;
    }
}