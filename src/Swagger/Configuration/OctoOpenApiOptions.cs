namespace Meshmakers.Octo.Services.Swagger.Configuration;

public class OctoOpenApiOptions
{
    /// <summary>
    /// Gets or sets the title of the API
    /// </summary>
    public string? ApiTitle { get; set; }

    /// <summary>
    /// Gets or sets the description of the API
    /// </summary>
    public string? ApiDescription { get; set; }

    /// <summary>
    /// Gets or sets the client id for Swagger UI authentication
    /// </summary>
    public string? ClientId { get; set; }
    public string? AppName { get; set; }

    public string? AuthorityUrl { get; set; }

    public IDictionary<string, string> Scopes { get; set; } = new Dictionary<string, string>();
}