using System.Data;
using Dapper;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Services.StreamData.Dapper;

/// <summary>
/// Allows dapper to handle CkId types
/// </summary>
internal class CkIdTypeHandler : SqlMapper.TypeHandler<CkId<CkTypeId>>
{
    public override void SetValue(IDbDataParameter parameter, CkId<CkTypeId>? value)
    {
        parameter.Value = value?.ToString();
    }

    public override CkId<CkTypeId>? Parse(object value)
    {
        if (value is not string)
        {
            throw new InvalidOperationException($"Could not parse CkId value '{value}'");
        }

        return new CkId<CkTypeId>((string)value);
    }
}