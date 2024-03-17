namespace Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;

/// <summary>
/// Message that signals that all jobs are removed of a specific schedule group
/// </summary>
/// <param name="ScheduleGroup">Name of the schedule group</param>
public record RemoveRecurringJobsByScheduleGroup(string ScheduleGroup);
