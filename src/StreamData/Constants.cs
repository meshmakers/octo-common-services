namespace Meshmakers.Octo.Services.Common.StreamData;

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
    /// Default stream data fields
    /// </summary>
    public static readonly string[] DefaultStreamDataFields = [Timestamp, RtId, CkTypeId];

    /// <summary>
    /// Date time format
    /// </summary>
    public static readonly string DateTimeFormat = "yyyy-MM-dd HH:mm:ss.fffZ";
}