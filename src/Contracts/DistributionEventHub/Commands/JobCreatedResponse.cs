namespace Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;

/// <summary>
///     Record for the response to a job created command.
/// </summary>
/// <param name="JobId"></param>
public record JobCreatedResponse(string JobId);