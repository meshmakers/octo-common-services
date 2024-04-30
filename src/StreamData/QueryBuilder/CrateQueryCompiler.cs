using System.Text;

namespace Meshmakers.Octo.Services.Common.StreamData.QueryBuilder;

/// <summary>
/// Crate Query Compiler
/// </summary>
public class CrateQueryCompiler
{
    /// <summary>
    /// Compiles the query
    /// </summary>
    /// <param name="queryBuilder"></param>
    /// <returns></returns>
    public string CompileQuery(CrateQueryBuilder queryBuilder)
    {
        var query = new StringBuilder();

        query.Append("SELECT ");
        var queryVariables = string.Join(", ", queryBuilder.Variables.Select(x => x.ToSelectString()));
        query.Append(queryVariables);

        query.Append($" FROM {queryBuilder.TenantId}");

        if (queryBuilder is { From: not null, To: not null })
        {
            query.Append(
                $" WHERE \"Timestamp\" >= '{queryBuilder.From.Value.ToString(Constants.DateTimeFormat)}' AND \"Timestamp\" <= '{queryBuilder.To.Value.ToString(Constants.DateTimeFormat)}'");
        }

        if (queryBuilder.HasAggregations)
        {
            query.Append(" GROUP BY ");
            query.Append(string.Join(", ", queryBuilder.Groupings.Select(x => x.ToGroupByString())));
        }
        
        if (queryBuilder.HasOrderBy)
        {
            query.Append(" ORDER BY ");
            query.Append(string.Join(", ", queryBuilder.OrderByVariables.Select(x => x.ToOrderByString())));
        }

        return query.ToString();
    }
}