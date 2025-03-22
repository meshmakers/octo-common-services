namespace Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;

/// <summary>
/// Defines the queue names
/// </summary>
public static class QueueNames
{
    /// <summary>
    /// Import a construction kit library command
    /// </summary>
    public const string ImportCkCommand = "octo::bot::import-ck"; 
    
    /// <summary>
    /// Import a runtime model command
    /// </summary>
    public const string ImportRtCommand = "octo::bot::import-rt";

    /// <summary>
    /// Export a runtime model by query command
    /// </summary>
    public const string ExportRtByQueryCommand = "octo::bot::export-rt-by-query";
    
    /// <summary>
    /// Export a runtime model by deep graph command
    /// </summary>
    public const string ExportRtByDeepGraphCommand = "octo::bot::export-rt-by-deep-graph";
    
    /// <summary>
    /// Remove recurring jobs by schedule group command
    /// </summary>
    public const string RemoveRecurringJobsByScheduleGroupCommand = "octo::bot::remove-recurring-jobs-by-schedule-group";
    
    /// <summary>
    /// Create identity data command
    /// </summary>
    public const string CreateIdentityDataCommand = "octo::identity::create-identity-data";
    
    /// <summary>
    /// The pipeline trigger channel name
    /// </summary>
    public const string PipelineTriggerChannelName = "octo::bot::pipeline-trigger";
    
    /// <summary>
    /// The pipeline trigger queue
    /// </summary>
    public const string PipelineTriggerQueue = $"queue:{PipelineTriggerChannelName}";
    
    /// <summary>
    /// Execute mesh pipeline command
    /// </summary>
    public const string ExecuteMeshPipelineCommand = "octo::com-controller::execute-mesh-pipeline";
    
    /// <summary>
    /// Execute send notification command
    /// </summary>
    public const string SendNotificationCommand = "octo::com-controller::send-notification-pipeline";
}