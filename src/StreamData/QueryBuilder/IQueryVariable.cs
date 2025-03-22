using Meshmakers.Octo.StreamData.Dtos;

namespace Meshmakers.Octo.StreamData.QueryBuilder;

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
    /// Sort order of the variable
    /// </summary>
    SortOrderDto? SortOrder { get; set; }
    
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
    /// Converts the variable to an order by string
    /// </summary>
    /// <returns></returns>
    string ToOrderByString();
    
    /// <summary>
    /// Name
    /// </summary>
    string Name { get; }
    
    /// <summary>
    /// Indicates whether the variable is a data variable (stored in dynamic data column)
    /// </summary>
    bool IsDataVariable { get; }

    /// <summary>
    /// Adds items to the VariableIn collection
    /// </summary>
    /// <param name="items"></param>
    void AddWhereInListItems(string[] items);

    /// <summary>
    /// Returns something like 'where "RtId" in ('65dc6d24cc529cdc46c84fcc', '65dc6d24cc529cdc46c84fcb')
    /// </summary>
    /// <returns></returns>
    string ToVariableInListString();
    
    bool HasVariableInListVariables { get; }
}