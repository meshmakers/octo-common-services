using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;

namespace Meshmakers.Octo.Services.Infrastructure.Services;

/// <summary>
///     <see cref="IArchiveAuditTrail"/> implementation that publishes archive lifecycle events
///     (concept §14) over the distribution event hub. Status names are sent as strings so
///     consumers don't need a shared enum dependency.
/// </summary>
internal class EventBusArchiveAuditTrail(
    ILogger<EventBusArchiveAuditTrail> logger,
    IDistributionEventHubService distributionEventHubService)
    : IArchiveAuditTrail
{
    public Task RecordTransitionAsync(
        string tenantId,
        OctoObjectId archiveRtId,
        CkArchiveStatus from,
        CkArchiveStatus to,
        string? reason)
    {
        logger.LogDebug(
            "Publishing archive transition for TenantId: {TenantId}, ArchiveRtId: {ArchiveRtId}, {FromStatus} -> {ToStatus}",
            tenantId, archiveRtId, from, to);

        return distributionEventHubService.PublishAsync(new ArchiveStatusTransition(
            tenantId,
            archiveRtId.ToString()!,
            from.ToString(),
            to.ToString(),
            reason,
            Guid.NewGuid(),
            DateTime.UtcNow));
    }

    public Task RecordDeletionAsync(string tenantId, OctoObjectId archiveRtId, CkArchiveStatus statusAtDeletion)
    {
        logger.LogDebug(
            "Publishing archive deletion for TenantId: {TenantId}, ArchiveRtId: {ArchiveRtId}, was {StatusAtDeletion}",
            tenantId, archiveRtId, statusAtDeletion);

        return distributionEventHubService.PublishAsync(new ArchiveDeleted(
            tenantId,
            archiveRtId.ToString()!,
            statusAtDeletion.ToString(),
            Guid.NewGuid(),
            DateTime.UtcNow));
    }
}
