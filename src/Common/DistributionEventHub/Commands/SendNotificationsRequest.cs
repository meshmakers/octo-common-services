using Meshmakers.Octo.Services.Common.DistributionEventHub.Commands.Payloads;

namespace Meshmakers.Octo.Services.Common.DistributionEventHub.Commands;

/// <summary>
///     Arguments for sending a notification
/// </summary>
public record SendNotificationsRequest : CommandBaseRequest
{
    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="tenantId">Tenant id</param>
    public SendNotificationsRequest(string tenantId)
        : base(tenantId)
    {
    }

    /// <summary>
    ///     Gets or sets the clients to create
    /// </summary>
    // ReSharper disable once CollectionNeverUpdated.Global
    public required ICollection<DistNotificationDto> Notifications { get; set; }
}