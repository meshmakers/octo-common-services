using Meshmakers.Octo.Services.Common.StreamData.Dtos;
using Meshmakers.Octo.Services.Common.StreamData.QueryBuilder.Decorators;

namespace Meshmakers.Octo.Services.Common.StreamData.QueryBuilder;

/// <summary>
/// Query Builder
/// </summary>
public class CrateQueryBuilder
{
    /// <summary>
    /// All Variables in the query
    /// </summary>
    internal List<IQueryVariable> Variables { get; } = new();

    /// <summary>
    /// All variables to ordered by
    /// </summary>
    internal List<IQueryVariable> OrderByVariables { get; } = new();

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
    internal bool HasAggregations => Variables.Any(x => x.AggregationFunction != null);
    
    /// <summary>
    /// 
    /// </summary>
    internal bool HasOrderBy => OrderByVariables.Count > 0;

    /// <summary>
    /// Variables to be included in the group by clause
    /// </summary>
    internal IEnumerable<IQueryVariable> Groupings => Variables.Where(x => x.AggregationFunction == null);
    
    internal int? Limit { get; private set; }
    
    internal int Offset { get; private set; }

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
        variable = new OrderByDecorator(variable);
        Variables.Add(variable);
        return this;
    }
    
    
    
    /// <summary>
    /// Includes all default variables that are available for every type
    /// </summary>
    public CrateQueryBuilder IncludeDefaultVariables()
    {
        foreach(var StreamDataField in Constants.DefaultStreamDataFields)
        {
            IQueryVariable variable = new QueryVariable(StreamDataField, null, null, false);
            variable = new QuotationDecorator(variable);
            variable = new VariableAliasDecorator(variable);
            variable = new OrderByDecorator(variable);
            Variables.Add(variable);
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
        variable = new OrderByDecorator(variable);
        Variables.Add(variable);
        return this;
    }

    /// <summary>
    /// Adds a 
    /// </summary>
    /// <param name="nameOrAlias"></param>
    /// <param name="sortOrder"></param>
    /// <returns></returns>
    /// <exception cref="QueryBuilderException"></exception>
    public CrateQueryBuilder OrderBy(string nameOrAlias, SortOrderDto sortOrder)
    {
        var variable = Variables.FirstOrDefault(x=> x.Name == nameOrAlias || x.Alias == nameOrAlias);
        if (variable == null)
        {
            throw QueryBuilderException.OrderByVariableNotFound(nameOrAlias);
        }

        variable.SortOrder = sortOrder;
        
        OrderByVariables.Add(variable);
        return this;
    }

    /// <summary>
    /// Sets a limit on the query
    /// </summary>
    /// <param name="limit">must be a positive integer (limit>0)</param>
    /// <returns></returns>
    /// <exception cref="QueryBuilderException"></exception>
    public CrateQueryBuilder WithLimit(int limit)
    {
        if (limit < 1)
        {
            throw QueryBuilderException.LimitMustBeGreaterThanZero();
        }
        Limit = limit;
        return this;
    }
    
    /// <summary>
    /// Sets an offset on the query
    /// </summary>
    /// <param name="offset">must be a positive integer including zero (offset>=0)</param>
    /// <returns></returns>
    /// <exception cref="QueryBuilderException"></exception>
    public CrateQueryBuilder WithOffset(int offset)
    {
        if (offset < 0)
        {
            throw QueryBuilderException.OffsetMustBeGreaterThanZero();
        }
        Offset = offset;
        return this;
    }
    
    
}