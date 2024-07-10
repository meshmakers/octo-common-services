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

    /// <summary>
    /// Helper method to create a connection string from the configuration
    /// </summary>
    /// <param name="host"></param>
    /// <param name="user"></param>
    /// <param name="password"></param>
    /// <returns></returns>
    public void ConnectionStringFromConfiguration(string host, string user, string? password)
    {
        if (password != null)
        {
            ConnectionString = $"Host={host};Username={user};SSL Mode=Prefer";
        }
        
        ConnectionString = $"Host={host};Username={user};Password={password};SSL Mode=Prefer";
    }

}