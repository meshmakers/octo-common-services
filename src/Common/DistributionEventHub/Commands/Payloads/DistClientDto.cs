namespace Meshmakers.Octo.Services.Common.DistributionEventHub.Commands.Payloads;

/// <summary>
/// Represents a client.
/// </summary>
/// <param name="ClientId">Client id.</param>
/// <param name="ClientName">Client name.</param>
/// <param name="ClientUri">Client uri.</param>
public record DistClientDto(string ClientId, string ClientName, string ClientUri)
{
    /// <summary>
    /// Gets or sets allowed grant types.
    /// </summary>
    public string[] AllowedGrantTypes { get; init; } = null!;

    /// <summary>
    /// Gets or sets if a consent is required.
    /// </summary>
    public bool RequireConsent { get; init; }

    /// <summary>
    /// Gets or sets redirect uris.
    /// </summary>
    public string[] RedirectUris { get; init; } = null!;

    /// <summary>
    /// Gets or sets post logout redirect uris.
    /// </summary>
    public string[] PostLogoutRedirectUris { get; init; } = null!;

    /// <summary>
    /// Gets or sets allowed cors origins.
    /// </summary>
    public string[] AllowedCorsOrigins { get; init; } = null!;

    /// <summary>
    /// Gets or sets allowed scopes.
    /// </summary>
    public string[] AllowedScopes { get; init; } = null!;

    /// <summary>
    /// Gets or sets if offline access is allowed.
    /// </summary>
    public bool AllowOfflineAccess { get; init; }
}