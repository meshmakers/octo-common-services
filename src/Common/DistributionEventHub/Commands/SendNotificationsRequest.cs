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
        Notifications = new List<DistNotificationDto>();
        SendAt = DateTime.UtcNow;
    }
    
    /// <summary>
    /// Gets or sets the send at date
    /// </summary>
    public DateTime SendAt { get; }

    /// <summary>
    ///     Gets or sets the clients to create
    /// </summary>
    // ReSharper disable once CollectionNeverUpdated.Global
    public ICollection<DistNotificationDto> Notifications { get; }
}