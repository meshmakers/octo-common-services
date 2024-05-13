namespace Meshmakers.Octo.Services.Common.StreamData.Configuration;

/// <summary>
/// Configuration for the stream data database.
/// </summary>
public class StreamDataConfiguration
{
    /// <summary>
    /// Connection string for the stream data database.
    /// </summary>
    public required string ConnectionString { get; set; }

    /// <summary>
    /// Duration for which connections are cached.
    /// </summary>
    public TimeSpan ConnectionCacheDuration { get; set; } = Constants.DefaultConnectionCacheDuration;

}