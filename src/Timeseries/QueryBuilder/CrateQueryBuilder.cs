using Meshmakers.Octo.Services.Common.Timeseries.Dtos;
using Meshmakers.Octo.Services.Common.Timeseries.QueryBuilder.Decorators;

namespace Meshmakers.Octo.Services.Common.Timeseries.QueryBuilder;

/// <summary>
/// Query Builder
/// </summary>
public class CrateQueryBuilder
{
    private readonly HashSet<IQueryVariable> _variablesToInclude = new();

    /// <summary>
    /// All Variables in the query
    /// </summary>
    internal HashSet<IQueryVariable> Variables => _variablesToInclude;

    /// <summary>
    /// Tenant Id
    /// </summary>
    internal string TenantId { get; }


    /// <summary>
    /// Time Filter
    /// </summary>
    internal DateTime? From { get; private set; }

    /// <summary>
    /// Time Filter
    /// </summary>
    internal DateTime? To { get; private set; }

    /// <summary>
    /// 
    /// </summary>
    internal bool HasAggregations => _variablesToInclude.Any(x => x.AggregationFunction != null);

    /// <summary>
    /// Variables to be included in the group by clause
    /// </summary>
    internal IEnumerable<IQueryVariable> Groupings => _variablesToInclude.Where(x => x.AggregationFunction == null);

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="tenantId"></param>
    public CrateQueryBuilder(string tenantId)
    {
        TenantId = tenantId;
    }

    /// <summary>
    /// Adds a time filter to the query
    /// </summary>
    /// <param name="from"></param>
    /// <param name="to"></param>
    /// <returns></returns>
    public CrateQueryBuilder WithTimeFilter(DateTime from, DateTime to)
    {
        From = from;
        To = to;
        return this;
    }

    /// <summary>
    /// Adds a normal variable to the query
    /// </summary>
    /// <param name="variableName"></param>
    /// <param name="alias"></param>
    /// <param name="aggregationFunction"></param>
    /// <param name="isDataVariable"></param>
    /// <returns></returns>
    public CrateQueryBuilder AddVariable(string variableName, string? alias = null, AggregationFunctionDto? aggregationFunction = null, bool isDataVariable = false)
    {
        IQueryVariable variable = new QueryVariable(variableName, alias, aggregationFunction, isDataVariable);
        variable = new DataVariableDecorator(variable);
        variable = new QuotationDecorator(variable);
        variable = new VariableAliasDecorator(variable);
        _variablesToInclude.Add(variable);
        return this;
    }
    
    
    
    /// <summary>
    /// Includes all default variables that are available for every type
    /// </summary>
    public CrateQueryBuilder IncludeDefaultVariables()
    {
        foreach(var timeSeriesField in Constants.DefaultTimeSeriesFields)
        {
            IQueryVariable variable = new QueryVariable(timeSeriesField, null, null, false);
            variable = new QuotationDecorator(variable);
            variable = new VariableAliasDecorator(variable);
            _variablesToInclude.Add(variable);
        }
        
        return this;
    }


    /// <summary>
    /// Adds an aggregation variable to the query
    /// </summary>
    /// <param name="name"></param>
    /// <param name="aggregate"></param>
    /// <param name="alias"></param>
    /// <param name="isDataVariable"></param>
    /// <returns></returns>
    public CrateQueryBuilder AddAggregationVariable(string name, AggregationFunctionDto aggregate, string? alias = null, bool isDataVariable = false)
    {
        var variableAlias = alias ?? $"{aggregate.ToString()}_{name}";
        IQueryVariable variable = new QueryVariable(name, variableAlias, aggregate, isDataVariable);
        variable = new DataVariableDecorator(variable);
        variable = new QuotationDecorator(variable);
        variable = new AggregationFunctionDecorator(variable, aggregate);
        variable = new VariableAliasDecorator(variable);
        _variablesToInclude.Add(variable);
        return this;
    }
    
    
}