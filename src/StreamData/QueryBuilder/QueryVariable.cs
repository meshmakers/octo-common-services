using Meshmakers.Octo.Services.StreamData.Dtos;

namespace Meshmakers.Octo.Services.StreamData.QueryBuilder;

/// <summary>
/// Query Variable
/// </summary>
internal record QueryVariable : IQueryVariable
{
    private readonly List<string> _variableContainedInList = [];

    /// <summary>
    /// Query Variable
    /// </summary>
    /// <param name="name"></param>
    /// <param name="alias"></param>
    /// <param name="AggregationFunction"></param>
    /// <param name="isDataVariable"></param>
    public QueryVariable(string name,
        string? alias,
        AggregationFunctionDto? AggregationFunction,
        bool isDataVariable = false)
    {

        this.AggregationFunction = AggregationFunction;
        Name = isDataVariable ? $"data['{name}']" : name;
        IsDataVariable = isDataVariable;
        
        if (AggregationFunction != null)
        {
            Name = $"{AggregationFunction.ToString()!.ToUpper()}(\"{Name}\")";
        }

        if (alias == null)
        {
            Alias = Name;
        }
        else
        {
            Alias = alias;
        }
            
        
    }

    /// <summary>
    /// Adds items to the VariableIn collection
    /// </summary>
    /// <param name="items"></param>
    public void AddWhereInListItems(string[] items)
    {
        _variableContainedInList.AddRange(items);
    }

    public string ToVariableInListString()
    {
        var escapedValue = _variableContainedInList.Select(x => $"'{x}'");
        var list = string.Join(", ", escapedValue);
        return $"\"{Alias ?? Name}\" IN ({list})";
    }

    public bool HasVariableInListVariables => _variableContainedInList.Count > 0;
    
    /// <inheritdoc />
    public SortOrderDto? SortOrder { get; set; }

    /// <summary></summary>
    public string Name { get; init; }

    /// <summary></summary>
    public string? Alias { get; init; }

    /// <summary></summary>
    public AggregationFunctionDto? AggregationFunction { get; init; }

    /// <summary></summary>
    public bool IsDataVariable { get; init; }


    /// <inheritdoc />

    public string ToSelectString()
    {
        if (AggregationFunction == null)
        {
            return Alias == Name ? $"\"{Name}\"" : $"\"{Name}\" AS \"{Alias}\"";
        }
        return Alias == Name ? Name : $"{Name} AS \"{Alias}\"";
    }

    /// <inheritdoc />
    public string ToGroupByString()
    {
        return $"\"{Alias}\"";
    }
    
    public string ToOrderByString()
    {
        return $"\"{Alias}\" {GetSortOrderString()}";
    }

    private string GetSortOrderString()
    {
        return SortOrder == SortOrderDto.Descending ? "DESC" : "ASC";
    }

    public void Deconstruct(out string name, out string? alias, out AggregationFunctionDto? aggregationFunction, out bool isDataVariable)
    {
        name = Name;
        alias = Alias;
        aggregationFunction = AggregationFunction;
        isDataVariable = IsDataVariable;
    }
}