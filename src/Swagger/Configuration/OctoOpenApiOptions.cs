using System.Reflection;

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
    
    /// <summary>
    /// Gets or sets the data transfer object assemblies to be included in the Swagger documentation from their XML documentation files
    /// </summary>
    /// <remarks>
    /// XML documentation files for these assemblies must be present in the same directory as the assembly
    /// </remarks>
    public IEnumerable<Assembly> XmlDocDataTransferObjectAssemblies { get; set; } = new List<Assembly>();
    
    /// <summary>
    /// A list of assemblies that contain Web API controllers to be included in the Swagger documentation from their XML documentation files
    /// </summary>
    /// <remarks>
    /// XML documentation files for these assemblies must be present in the same directory as the assembly
    /// </remarks>
    public IEnumerable<Assembly> XmlDocOperationAssemblies { get; set; } = new List<Assembly>();
}