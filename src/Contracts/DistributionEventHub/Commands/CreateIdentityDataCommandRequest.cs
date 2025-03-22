using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands.Payloads;

namespace Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;

/// <summary>
///     Create client at identity service argument
/// </summary>
public record CreateIdentityDataCommandRequest : CommandBaseRequest
{
    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="tenantId">Tenant id</param>
    public CreateIdentityDataCommandRequest(string tenantId)
        : base(tenantId)
    {
    }

    /// <summary>
    ///     Gets or sets the clients to create
    /// </summary>
    // ReSharper disable once CollectionNeverUpdated.Global
    public ICollection<DistClientDto>? Clients { get; set; }

    /// <summary>
    ///     Gets or sets the API scopes to create
    /// </summary>
    // ReSharper disable once CollectionNeverUpdated.Global
    public ICollection<DistApiScopeDto>? ApiScopes { get; set; }

    /// <summary>
    ///     Gets or sets the API resources to create
    /// </summary>
    // ReSharper disable once CollectionNeverUpdated.Global
    public ICollection<DistApiResourcesDto>? ApiResources { get; set; }
}