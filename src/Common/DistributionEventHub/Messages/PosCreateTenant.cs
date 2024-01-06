namespace Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;

/// <summary>
///     Message in distribution event hub after a tenant got created
/// </summary>
public record PosCreateTenant(string TenantId)
{
}