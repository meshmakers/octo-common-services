namespace Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands.Payloads;

/// <summary>
/// Represents a notification.
/// </summary>
/// <param name="Subject">Subject of the notification.</param>
/// <param name="Body">Body of the notification.</param>
/// <param name="Recipient">Recipient of the notification.</param>
/// <param name="Cc">Carbon copy recipient of the notification.</param>
/// <param name="Bcc">Blind carbon copy recipient of the notification.</param>
// ReSharper disable once ClassNeverInstantiated.Global
public record DistNotificationDto(string Subject, string? Body, string? Recipient, string? Cc, string? Bcc);
