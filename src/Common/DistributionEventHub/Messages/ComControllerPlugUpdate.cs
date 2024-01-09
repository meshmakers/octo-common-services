using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages.Payloads;

namespace Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;

/// <summary>
/// Communication Controller plug update
/// </summary>
public record ComControllerPlugUpdate(string TenantId, IEnumerable<PlugDescription> Plugs);
