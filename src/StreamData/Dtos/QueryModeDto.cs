namespace Meshmakers.Octo.Services.Common.StreamData.Dtos;

/// <summary>
/// Defines the kind of query to be executed
/// </summary>
public enum QueryModeDto
{
    /// <summary>
    /// Returns the real values
    /// </summary>
    Default,
    
    /// <summary>
    /// Aggregates the values
    /// </summary>
    Aggregation,
    
    /// <summary>
    /// Interpolates the values
    /// </summary>
    Interpolation
}