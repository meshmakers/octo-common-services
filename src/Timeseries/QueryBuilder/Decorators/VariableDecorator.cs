using Meshmakers.Octo.Services.Common.Timeseries.Dtos;

namespace Meshmakers.Octo.Services.Common.Timeseries.QueryBuilder.Decorators;

/// <summary>
/// Query Decorator
/// </summary>
internal class VariableDecorator : IQueryVariable
{
    /// <summary>
    /// Query Variable
    /// </summary>
    protected readonly IQueryVariable Inner;

    /// <summary>
    /// ctor
    /// </summary>
    /// <param name="inner"></param>
    public VariableDecorator(IQueryVariable inner)
    {
        Inner = inner;
    }

    public AggregationFunctionDto? AggregationFunction => Inner.AggregationFunction;
    public SortOrderDto? SortOrder
    {
        get => Inner.SortOrder;
        set => Inner.SortOrder = value;
    }


    public virtual string? Alias => Inner.Alias;

    public virtual string Name => Inner.Name;

    public virtual bool IsDataVariable => Inner.IsDataVariable;

    /// <inheritdoc />
    public virtual string ToSelectString()
    {
        return Inner.ToSelectString();
    }

    public virtual string ToGroupByString()
    {
        return Inner.ToGroupByString();
    }

    public virtual string ToOrderByString()
    {
        return Inner.ToOrderByString();
    }
}