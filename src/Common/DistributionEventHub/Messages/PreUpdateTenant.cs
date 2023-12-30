namespace Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;

/// <summary>
/// Message in distribution event hub before a tenant gets modified
/// </summary>
public record PreUpdateTenant(string TenantId)
{
}