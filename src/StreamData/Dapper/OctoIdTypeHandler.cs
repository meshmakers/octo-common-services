using System.Data;
using Dapper;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Services.StreamData.Dapper;

internal class OctoIdTypeHandler : SqlMapper.TypeHandler<OctoObjectId>
{
    public override void SetValue(IDbDataParameter parameter, OctoObjectId value)
    {
        parameter.Value = value.ToString();
    }

    public override OctoObjectId Parse(object value)
    {
        if(value is not string s || !OctoObjectId.TryParse(s, out var result))
        {
            throw new InvalidOperationException($"Could not parse OctoId value '{value}'");
        }

        return result;
    }
}