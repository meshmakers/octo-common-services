using Meshmakers.Octo.Services.Common.Timeseries.Dtos;

namespace Meshmakers.Octo.Services.Common.Timeseries.QueryBuilder.Decorators;

internal class AggregationFunctionDecorator : VariableDecorator
{
    private readonly AggregationFunctionDto _aggregationFunctionDto;

    public AggregationFunctionDecorator(IQueryVariable inner, AggregationFunctionDto aggregationFunctionDto) : base(inner)
    {
        _aggregationFunctionDto = aggregationFunctionDto;
    }

    public override string ToSelectString()
    {
        return $"{_aggregationFunctionDto.ToString().ToUpper()}({Inner.ToSelectString()})";
    }
}