namespace Meshmakers.Octo.Services.Common.Timeseries.QueryBuilder.Decorators;

internal class VariableAliasDecorator(IQueryVariable inner) : VariableDecorator(inner)
{
    public override string ToSelectString()
    {
        return Inner.Alias == null ? Inner.ToSelectString() : $"{Inner.ToSelectString()} AS \"{Inner.Alias}\"";
    }
}