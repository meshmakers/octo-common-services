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
    /// Export a runtime model by query command
    /// </summary>
    public const string ExportRtByQueryCommand = "bot::export-rt-by-query";
    
    /// <summary>
    /// Export a runtime model by deep graph command
    /// </summary>
    public const string ExportRtByDeepGraphCommand = "bot::export-rt-by-query";
    
    /// <summary>
    /// Remove recurring jobs by schedule group command
    /// </summary>
    public const string RemoveRecurringJobsByScheduleGroupCommand = "bot::remove-recurring-jobs-by-schedule-group";
    
    /// <summary>
    /// Create identity data command
    /// </summary>
    public const string CreateIdentityDataCommand = "identity::create-identity-data";
    
    /// <summary>
    /// The pipeline trigger channel name
    /// </summary>
    public const string PipelineTriggerChannelName = "bot::pipeline-trigger";
    
    /// <summary>
    /// The pipeline trigger queue
    /// </summary>
    public const string PipelineTriggerQueue = $"queue:{PipelineTriggerChannelName}";
}