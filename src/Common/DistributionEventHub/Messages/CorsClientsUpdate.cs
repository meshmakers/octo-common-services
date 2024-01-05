namespace Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;

/// <summary>
///     Command to update the CORS clients for a tenant
/// </summary>
/// <param name="TenantId"></param>
public record CorsClientsUpdate(string? TenantId);