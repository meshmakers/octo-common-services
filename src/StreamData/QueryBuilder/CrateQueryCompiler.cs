using System.Text;
using Meshmakers.Octo.Services.StreamData.Dtos;

namespace Meshmakers.Octo.Services.StreamData.QueryBuilder;

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
        
        // figure out if we want to downsample or interpolate

        if (queryBuilder.QueryMode == QueryModeDto.Downsampling)
        {
            if (queryBuilder.TimeStampVariable == null)
            {
                throw QueryBuilderException.InterpolationOrDownsamplingNeedToIncludeTimeStampVariable();
            }
            
            var interval = queryBuilder.To!.Value - queryBuilder.From!.Value;
            var intervalSeconds = (int)interval.TotalSeconds / queryBuilder.Limit;
            
            query.Append($"DATE_BIN('{intervalSeconds} seconds'::INTERVAL, \"Timestamp\", 0) AS \"T\", ");
        }
        else if(queryBuilder.TimeStampVariable != null)
        {
            var timeStampVariable = queryBuilder.TimeStampVariable;
            query.Append(timeStampVariable.ToSelectString() + ", ");
        }
        
        var queryVariables = string.Join(", ", queryBuilder.QueryVariablesWithoutTimestamp.Select(x => x.ToSelectString()));
        query.Append(queryVariables);

        query.Append($" FROM {queryBuilder.TenantId}");

        if (queryBuilder.VariableInListVariables.Any() || queryBuilder is { From: not null, To: not null } || queryBuilder.CkTypeId != null)
        {
            // we can only have one where clause, but we can connect it with AND
            query.Append(" WHERE ");
        }
        
        if(queryBuilder.CkTypeId != null)
        {
            query.Append($"\"CkTypeId\" = '{queryBuilder.CkTypeId}'");

            if (queryBuilder.VariableInListVariables.Any() || queryBuilder is { From: not null, To: not null })
            {
                query.Append(" AND ");
            }
            
        }
        
        if (queryBuilder.VariableInListVariables.Any())
        {
            query.Append(string.Join(" AND ",
                queryBuilder.VariableInListVariables.Select(x => x.ToVariableInListString())));

            if (queryBuilder is { From: not null, To: not null })
            {
                // if we have a time filter as well, we have to connect the filter conditions with an AND
                query.Append(" AND ");
            }
        }

        if (queryBuilder is { From: not null, To: not null })
        {
            query.Append(
                $"\"Timestamp\" >= '{queryBuilder.From.Value.ToString(Constants.DateTimeFormat)}' AND \"Timestamp\" <= '{queryBuilder.To.Value.ToString(Constants.DateTimeFormat)}'");
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

        if (queryBuilder.Limit is not null)
        {
            query.Append($" LIMIT {queryBuilder.Limit}");
        }

        return query.ToString();
    }
}