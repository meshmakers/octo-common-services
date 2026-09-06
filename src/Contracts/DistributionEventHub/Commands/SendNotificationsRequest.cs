using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands.Payloads;

namespace Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;

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
    public DateTime SendAt { get; set; }

    /// <summary>
    ///     Gets or sets the notifications to send.
    /// </summary>
    /// <remarks>
    ///     AB#5136: settable so the message deserializes across the distribution-event-hub. A
    ///     get-only collection combined with the parameterized constructor is NOT populated by
    ///     System.Text.Json (MassTransit's serializer) on the consumer side — the list arrived empty
    ///     at <c>FromSendNotification@1</c>, so identity's e-mail OTP / welcome / reset mails were
    ///     published but never delivered. The wire format is unchanged (getters were always
    ///     serialized); only deserialization needed a setter.
    /// </remarks>
    public ICollection<DistNotificationDto> Notifications { get; set; }
}