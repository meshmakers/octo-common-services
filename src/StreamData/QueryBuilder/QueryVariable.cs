using Meshmakers.Octo.Services.Common.StreamData.Dtos;

namespace Meshmakers.Octo.Services.Common.StreamData.QueryBuilder;

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
    /// <inheritdoc />
    public SortOrderDto? SortOrder { get; set; }
    
    /// <inheritdoc />
    public bool HasVariableInListVariables => false;

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

    /// <summary>
    /// Adds items to the VariableIn collection
    /// </summary>
    /// <param name="items"></param>
    public void AddWhereInListItems(string[] items)
    {
    }


    public string ToVariableInListString() => "";
}