namespace Meshmakers.Octo.Services.StreamData.Dtos;

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
    /// Downsampling of the values
    /// </summary>
    Downsampling,
    
    /// <summary>
    /// Interpolates the values
    /// </summary>
    Interpolation
}