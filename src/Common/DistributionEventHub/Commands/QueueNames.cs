namespace Meshmakers.Octo.Services.Common.DistributionEventHub.Commands;

/// <summary>
/// Defines the queue names
/// </summary>
public static class QueueNames
{
    /// <summary>
    /// Import a construction kit library command
    /// </summary>
    public const string ImportCkCommand = "bot::import-ck"; 
    
    /// <summary>
    /// Import a runtime model command
    /// </summary>
    public const string ImportRtCommand = "bot::import-rt";

    /// <summary>
    /// Export a runtime model command
    /// </summary>
    public const string ExportRtCommand = "bot::export-rt";
    
    /// <summary>
    /// Remove recurring jobs by schedule group command
    /// </summary>
    public const string RemoveRecurringJobsByScheduleGroupCommand = "bot::remove-recurring-jobs-by-schedule-group";
    
    /// <summary>
    /// Create identity data command
    /// </summary>
    public const string CreateIdentityDataCommand = "identity::create-identity-data";
    
    /// <summary>
    /// The pipeline trigger queue
    /// </summary>
    public const string PipelineTriggerQueue = "queue:bot::pipeline-trigger";
}