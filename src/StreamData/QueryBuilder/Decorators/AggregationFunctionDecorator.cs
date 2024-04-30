using Meshmakers.Octo.Services.Common.StreamData.Dtos;

namespace Meshmakers.Octo.Services.Common.StreamData.QueryBuilder.Decorators;

internal class AggregationFunctionDecorator(IQueryVariable inner, AggregationFunctionDto aggregationFunctionDto)
    : VariableDecorator(inner)
{
    public override string ToSelectString()
    {
        return $"{aggregationFunctionDto.ToString().ToUpper()}({Inner.ToSelectString()})";
    }
}