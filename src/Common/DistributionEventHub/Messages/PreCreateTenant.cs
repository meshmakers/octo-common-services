namespace Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;

/// <summary>
///     Message in distribution event hub before a tenant gets created
/// </summary>
public record PreCreateTenant(string TenantId, Guid CorrelationId)
{
}