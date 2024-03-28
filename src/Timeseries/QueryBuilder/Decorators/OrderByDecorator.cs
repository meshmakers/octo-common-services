using Meshmakers.Octo.Services.Common.Timeseries.Dtos;

namespace Meshmakers.Octo.Services.Common.Timeseries.QueryBuilder.Decorators;

/// <summary>
/// 
/// </summary>
/// <param name="inner"></param>
internal class OrderByDecorator(IQueryVariable inner) : VariableDecorator(inner)
{
    public override string ToOrderByString()
    {
        return $"{Inner.ToOrderByString()} {GetSortOrderString()}";
    }

    private string GetSortOrderString()
    {
        return Inner.SortOrder == SortOrderDto.Descending ? "DESC" : "ASC";
    }
}