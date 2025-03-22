using System.Data;
using System.Text.Json;
using Dapper;

namespace Meshmakers.Octo.StreamData.Dapper;

/// <summary>
/// Allows dapper to handle JSON types
/// </summary>
/// <typeparam name="T"></typeparam>
internal class JsonTypeHandler<T> : SqlMapper.TypeHandler<Json<T>>
{
    public override void SetValue(IDbDataParameter parameter, Json<T>? value)
    {
        if (value != null)
        {
            parameter.Value = JsonSerializer.Serialize(value.Value);
        }
    }

    public override Json<T> Parse(object value)
    {
        if (value is string json)
        {
            var result = JsonSerializer.Deserialize<T>(json);
            if (result is not null)
            {
                return new Json<T>(result);
            }
        }

        throw new InvalidOperationException($"Could not parse JSON value '{value}'");
    }
}