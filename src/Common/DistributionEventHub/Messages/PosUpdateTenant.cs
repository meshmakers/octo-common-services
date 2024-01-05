namespace Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;

/// <summary>
///     Message in distribution event hub after a tenant gets modified
/// </summary>
public record PosUpdateTenant(string TenantId)
{
}