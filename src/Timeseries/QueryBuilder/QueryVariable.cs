using Meshmakers.Octo.Services.Common.Timeseries.Dtos;

namespace Meshmakers.Octo.Services.Common.Timeseries.QueryBuilder;

/// <summary>
/// Query Variable
/// </summary>
/// <param name="Name"></param>
/// <param name="Alias"></param>
/// <param name="AggregationFunction"></param>
/// <param name="IsDataVariable"></param>
internal record QueryVariable(
    string Name,
    string? Alias,
    AggregationFunctionDto? AggregationFunction,
    bool IsDataVariable = false) : IQueryVariable
{
    public SortOrderDto? SortOrder { get; set; }

    /// <inheritdoc />
    public string ToSelectString()
    {
        return Name;
    }

    /// <inheritdoc />
    public string ToGroupByString()
    {
        return Alias ?? Name;
    }
    
    /// <inheritdoc />
    public string ToOrderByString()
    {
        return Alias ?? Name;
    }
}