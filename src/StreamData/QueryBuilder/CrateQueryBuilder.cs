using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Services.StreamData.Dtos;

namespace Meshmakers.Octo.Services.StreamData.QueryBuilder;

/// <summary>
/// Query Builder
/// </summary>
public class CrateQueryBuilder
{
    /// <summary>
    /// All Variables in the query
    /// </summary>
    internal List<IQueryVariable> Variables { get; } = new();
    
    internal IEnumerable<IQueryVariable> QueryVariablesWithoutTimestamp => Variables.Where(x => x.Name != "Timestamp");

    /// <summary>
    /// All variables to ordered by
    /// </summary>
    internal List<IQueryVariable> OrderByVariables { get; } = new();

    /// <summary>
    /// Time Stamp Variable
    /// </summary>
    internal IQueryVariable? TimeStampVariable => Variables.FirstOrDefault(x => x.Name == "Timestamp");

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

    internal IEnumerable<IQueryVariable> VariableInListVariables => Variables.Where(x => x.HasVariableInListVariables);

    internal List<StreamDataFieldFilterDto> FieldFilters { get; } = new();

    internal bool HasFieldFilters => FieldFilters.Count > 0;

    internal int? Limit { get; private set; }
    
    internal int? Offset { get; private set; }

    internal RtCkId<CkTypeId>? CkTypeId { get; private set; }

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
    /// Adds a type filter to the query
    /// </summary>
    /// <param name="ckTypeId"></param>
    /// <returns></returns>
    public CrateQueryBuilder WithCkTypeIdFilter(RtCkId<CkTypeId> ckTypeId)
    {
        this.CkTypeId = ckTypeId;
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
        
        var idx = Variables.FindIndex(x => x.Name == variable.Name || x.Alias == variable.Alias);
        if (idx != -1)
        {
            Variables[idx] = variable;
            return this;
        }
        Variables.Add(variable);
        return this;
    }
    
    
    
    /// <summary>
    /// Includes all default variables that are available for every type
    /// </summary>
    public CrateQueryBuilder IncludeDefaultVariables()
    {
        foreach(var streamDataField in Constants.DefaultStreamDataFields)
        {
            IQueryVariable variable = new QueryVariable(streamDataField, null, null, false);
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
    /// Add a list of values wher e
    /// </summary>
    /// <param name="nameOrAlias"></param>
    /// <param name="list"></param>
    /// <returns></returns>
    /// <exception cref="QueryBuilderException"></exception>
    public CrateQueryBuilder AddWhereIn(string nameOrAlias, string[] list)
    {
        var variable = Variables.FirstOrDefault(x => x.Name == nameOrAlias || x.Alias == nameOrAlias);
        if (variable == null)
        {
            throw QueryBuilderException.WhereInVariableNotFound(nameOrAlias);
        }

        variable.AddWhereInListItems(list);

        return this;
    }

    /// <summary>
    /// Adds a field filter condition to the WHERE clause
    /// </summary>
    /// <param name="fieldName">Column name</param>
    /// <param name="op">Comparison operator</param>
    /// <param name="value">Comparison value</param>
    /// <param name="isDataField">Whether the field is in the dynamic data column</param>
    /// <returns></returns>
    public CrateQueryBuilder AddFieldFilter(string fieldName, StreamDataFieldFilterOperator op, string value, bool isDataField = false)
    {
        FieldFilters.Add(new StreamDataFieldFilterDto(fieldName, op, value, isDataField));
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

    /// <summary>
    /// Adds a downsampling to the query
    /// </summary>
    /// <param name="limit">Amount of points</param>
    /// <param name="from"></param>
    /// <param name="to"></param>
    /// <returns></returns>
    public CrateQueryBuilder WithDownsampling(int limit, DateTime from, DateTime to)
    {
        From = from;
        To = to;
        QueryMode = QueryModeDto.Downsampling;
        Limit = limit;
        return this;
    }

    /// <summary>
    /// Defines what kind of query should be executed
    /// </summary>
    public QueryModeDto? QueryMode { get; set; }
}