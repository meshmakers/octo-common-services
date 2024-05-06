namespace Meshmakers.Octo.Services.Common.StreamData.QueryBuilder.Decorators;

/// <summary>
/// 
/// </summary>
internal class IsInListDecorator(IQueryVariable inner) : VariableDecorator(inner)
{
    private readonly List<string> _items = [];
    public override void AddWhereInListItems(string[] items)
    {
        _items.AddRange(items);
    }

    public override string ToVariableInListString()
    {
        var escapedValue = _items.Select(x => $"'{x}'");
        var list = string.Join(", ", escapedValue);
        return $"\"{Alias ?? Name}\" IN ({list})";
    }

    public override bool HasVariableInListVariables => _items.Count > 0;
}