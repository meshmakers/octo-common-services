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
    /// Checks if the given field name is a default stream data field (case-insensitive).
    /// </summary>
    public static bool IsDefaultField(string fieldName)
        => DefaultStreamDataFields.Any(f => string.Equals(f, fieldName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Returns the canonical PascalCase name of a default field, or null if not a default.
    /// </summary>
    public static string? GetDefaultFieldName(string fieldName)
        => DefaultStreamDataFields.FirstOrDefault(f => string.Equals(f, fieldName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Date time format
    /// </summary>
    public static readonly string DateTimeFormat = "yyyy-MM-dd HH:mm:ss.fffZ";
    
    /// <summary>
    /// Default connection cache duration
    /// </summary>
    public static readonly TimeSpan DefaultConnectionCacheDuration = TimeSpan.FromMinutes(5);
}