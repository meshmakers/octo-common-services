namespace Meshmakers.Octo.Services.Common.Timeseries.QueryBuilder.Decorators;

internal class QuotationDecorator(IQueryVariable inner) : VariableDecorator(inner)
{
    public override string ToSelectString()
    {
        return $"\"{Inner.ToSelectString()}\"";
    }
    
    public override string ToGroupByString()
    {
        return $"\"{Inner.ToGroupByString()}\"";
    }
}