using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages.Payloads;

namespace Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;

/// <summary>
/// Communication Controller socket update
/// </summary>
public record ComControllerSocketUpdate(string TenantId, IEnumerable<SocketDescription> Sockets);
