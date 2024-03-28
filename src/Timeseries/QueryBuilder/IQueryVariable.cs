using Meshmakers.Octo.Services.Common.Timeseries.Dtos;

namespace Meshmakers.Octo.Services.Common.Timeseries.QueryBuilder;

/// <summary>
/// Query Variable
/// </summary>
internal interface IQueryVariable
{
    /// <summary>
    /// Aggregation Function
    /// </summary>
    AggregationFunctionDto? AggregationFunction { get; }
    /// <summary>
    /// Alias
    /// </summary>
    string? Alias { get; }
    /// <summary>
    /// Converts the variable to a select string
    /// </summary>
    /// <returns></returns>
    string ToSelectString();

    /// <summary>
    /// Converts the variable to a group by string
    /// </summary>
    /// <returns></returns>
    string ToGroupByString();
    
    /// <summary>
    /// Name
    /// </summary>
    string Name { get; }
    
    /// <summary>
    /// Indicates whether the variable is a data variable (stored in dynamic data column)
    /// </summary>
    bool IsDataVariable { get; }
}