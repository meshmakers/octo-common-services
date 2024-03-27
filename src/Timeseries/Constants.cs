namespace Meshmakers.Octo.Services.Common.Timeseries;

/// <summary>
/// Constants for the time series service.
/// </summary>
public static class Constants
{
    /// <summary>
    /// RtId
    /// </summary>
    public const string RtId = "RtId";
    
    /// <summary>
    /// Timestamp
    /// </summary>
    public const string Timestamp = "Timestamp";
    
    /// <summary>
    /// CkId
    /// </summary>
    public const string CkTypeId = "CkTypeId";
    
    /// <summary>
    /// Default time series fields
    /// </summary>
    public static readonly string[] DefaultTimeSeriesFields = [RtId, Timestamp, CkTypeId];
}