namespace Meshmakers.Octo.Services.Common.DistributionEventHub.Commands;

/// <summary>
/// Message that signals that all jobs are removed of a specific schedule group
/// </summary>
/// <param name="ScheduleGroup">Name of the schedule group</param>
public record RemoveRecurringJobsByScheduleGroupRequest(string ScheduleGroup);
