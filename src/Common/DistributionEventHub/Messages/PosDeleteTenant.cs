namespace Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;

/// <summary>
///     Message in distribution event hub after a tenant got deleted
/// </summary>
public record PosDeleteTenant(string TenantId)
{
}