namespace Meshmakers.Octo.Services.StreamData;

/// <summary>
/// Constants for the stream data service.
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
    /// RtWellKnownName
    /// </summary>
    public const string RtWellKnownName = "RtWellKnownName";
    
    /// <summary>
    /// RtCreationDateTime
    /// </summary>
    public const string RtCreationDateTime = "RtCreationDateTime";
    
    /// <summary>
    /// RtChangedDateTime
    /// </summary>
    public const string RtChangedDateTime = "RtChangedDateTime";

    /// <summary>
    /// Default stream data fields
    /// </summary>
    public static readonly string[] DefaultStreamDataFields = [Timestamp, RtId, CkTypeId, RtWellKnownName, RtCreationDateTime, RtChangedDateTime];

    /// <summary>
    /// Date time format
    /// </summary>
    public static readonly string DateTimeFormat = "yyyy-MM-dd HH:mm:ss.fffZ";
    
    /// <summary>
    /// Default connection cache duration
    /// </summary>
    public static readonly TimeSpan DefaultConnectionCacheDuration = TimeSpan.FromMinutes(5);
}