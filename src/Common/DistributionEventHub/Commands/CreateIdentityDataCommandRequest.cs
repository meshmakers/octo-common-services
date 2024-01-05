using Meshmakers.Octo.Services.Common.DistributionEventHub.Commands.Payloads;

namespace Meshmakers.Octo.Services.Common.DistributionEventHub.Commands;

/// <summary>
///     Create client at identity service argument
/// </summary>
public record CreateIdentityDataCommandRequest : CommandBaseRequest
{
    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="tenantId">Tenant id</param>
    public CreateIdentityDataCommandRequest(string? tenantId)
        : base(tenantId)
    {
    }

    /// <summary>
    ///     Gets or sets the clients to create
    /// </summary>
    public ICollection<DistClientDto> Clients { get; set; } = null!;

    /// <summary>
    ///     Gets or sets the API scopes to create
    /// </summary>
    public ICollection<DistApiScopeDto> ApiScopes { get; set; } = null!;

    /// <summary>
    ///     Gets or sets the API resources to create
    /// </summary>
    public ICollection<DistApiResourcesDto> ApiResources { get; set; } = null!;
}