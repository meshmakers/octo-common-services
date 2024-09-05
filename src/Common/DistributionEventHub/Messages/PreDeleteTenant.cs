namespace Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;

/// <summary>
///     Message in distribution event hub before a tenant gets deleted
/// </summary>
public record PreDeleteTenant(string TenantId, Guid CorrelationId)
{
}