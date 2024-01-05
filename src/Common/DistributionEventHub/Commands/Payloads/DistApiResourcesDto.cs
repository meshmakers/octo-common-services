namespace Meshmakers.Octo.Services.Common.DistributionEventHub.Commands.Payloads;

/// <summary>
///     Represents an API resource.
/// </summary>
/// <param name="Name">Name of the resource.</param>
/// <param name="DisplayName">Display name of the resource.</param>
public record DistApiResourcesDto(string Name, string DisplayName)
{
    /// <summary>
    ///     Gets or sets description of the resource.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    ///     Gets or sets if resource is enabled.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    ///     Gets or sets scopes that the resource allows.
    /// </summary>
    public ICollection<string> Scopes { get; init; } = null!;
}