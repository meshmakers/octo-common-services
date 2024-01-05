namespace Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;

/// <summary>
///     Used to signal that identity provider configuration for a tenant is updated.
/// </summary>
/// <param name="TenantId"></param>
public record IdentityProviderUpdate(string? TenantId);