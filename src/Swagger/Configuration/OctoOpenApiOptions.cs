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
    
    /// <summary>
    /// Name of the app used for Swagger UI authentication
    /// </summary>
    public string? AppName { get; set; }

    /// <summary>
    /// Defines the authority URL
    /// </summary>
    public string? AuthorityUrl { get; set; }

    /// <summary>
    /// Mapping of scopes to descriptions
    /// </summary>
    public IDictionary<string, string> Scopes { get; set; } = new Dictionary<string, string>();
    
    /// <summary>
    /// Mapping of policy names to scopes
    /// </summary>
    public IDictionary<string, IEnumerable<string>> PolicyScopeMapping { get; set; } = new Dictionary<string, IEnumerable<string>>();
}