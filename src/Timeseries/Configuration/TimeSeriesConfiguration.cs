namespace Meshmakers.Octo.Services.Common.Timeseries.Configuration;

/// <summary>
/// Configuration for the time series database.
/// </summary>
public class TimeSeriesConfiguration
{
    /// <summary>
    /// Connection string for the time series database.
    /// </summary>
    public required string TimeSeriesConnectionString { get; set; }

}