namespace Meshmakers.Octo.Services.Common.Timeseries.QueryBuilder.Decorators;

internal class DataVariableDecorator(IQueryVariable inner) : VariableDecorator(inner)
{
    public override string ToSelectString()
    {
        return IsDataVariable ? $"data['{Inner.ToSelectString()}']" : Inner.ToSelectString();
    }
}