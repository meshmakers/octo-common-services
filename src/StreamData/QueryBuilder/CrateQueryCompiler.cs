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
        // Downsampling with aggregation uses a fundamentally different SQL structure
        // with generate_series LEFT JOIN to produce all time bins including empty ones
        if (queryBuilder.QueryMode == QueryModeDto.Downsampling && queryBuilder.HasAggregations)
        {
            return CompileDownsamplingQuery(queryBuilder);
        }

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

        AppendWhereClause(query, queryBuilder);

        if (queryBuilder.HasAggregations && queryBuilder.Groupings.Any())
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

        if (queryBuilder.Offset is not null)
        {
            query.Append($" OFFSET {queryBuilder.Offset}");
        }

        return query.ToString();
    }

    /// <summary>
    /// Compiles a downsampling query using generate_series LEFT JOIN to produce all time bins
    /// including empty ones. Adds COUNT(d."Timestamp") AS "__binCount" to detect empty bins
    /// (COUNT(*) would always return 1 due to LEFT JOIN producing a row for every bin).
    /// </summary>
    private static string CompileDownsamplingQuery(CrateQueryBuilder queryBuilder)
    {
        var query = new StringBuilder();

        var interval = queryBuilder.To!.Value - queryBuilder.From!.Value;
        var intervalSeconds = (int)interval.TotalSeconds / queryBuilder.Limit!.Value;
        var intervalLiteral = $"'{intervalSeconds} seconds'::INTERVAL";
        var fromLiteral = $"'{queryBuilder.From.Value.ToString(Constants.DateTimeFormat)}'::TIMESTAMP";
        // Compute exclusive upper bound: From + (Limit - 1) * interval
        // generate_series is inclusive on both ends, so we use Limit-1 intervals to get exactly Limit bins
        var seriesEnd = queryBuilder.From.Value.AddSeconds(intervalSeconds * (queryBuilder.Limit!.Value - 1));
        var seriesEndLiteral = $"'{seriesEnd.ToString(Constants.DateTimeFormat)}'::TIMESTAMP";

        // SELECT bins.ts AS "T", AGG(d."data['col']") AS "alias", COUNT(d."Timestamp") AS "__binCount"
        query.Append("SELECT bins.ts AS \"T\"");

        foreach (var variable in queryBuilder.QueryVariablesWithoutTimestamp)
        {
            query.Append(", ");
            // Add d. table alias prefix for data references in the LEFT JOIN
            if (variable.AggregationFunction != null)
            {
                // Aggregation: AVG("data['Voltage']") -> AVG(d."data['Voltage']")
                var selectStr = variable.ToSelectString();
                query.Append(selectStr.Replace("\"data[", "d.\"data["));
            }
            else
            {
                query.Append($"d.{variable.ToSelectString()}");
            }
        }

        query.Append(", COUNT(d.\"Timestamp\") AS \"__binCount\"");

        // FROM generate_series(from, seriesEnd, interval) — exactly Limit bins
        query.Append($" FROM generate_series({fromLiteral}, {seriesEndLiteral}, {intervalLiteral}) AS bins(ts)");

        // LEFT JOIN "tenant" AS d ON DATE_BIN(interval, d."Timestamp", from) = bins.ts — align data to From origin
        query.Append($" LEFT JOIN {queryBuilder.TenantId} AS d ON DATE_BIN({intervalLiteral}, d.\"Timestamp\", {fromLiteral}) = bins.ts");

        // All filter conditions go into the ON clause (not WHERE, since LEFT JOIN)
        if (queryBuilder.CkTypeId != null)
        {
            query.Append($" AND d.\"CkTypeId\" = '{queryBuilder.CkTypeId.SemanticVersionedFullName}'");
        }

        if (queryBuilder is { From: not null, To: not null })
        {
            query.Append($" AND d.\"Timestamp\" >= '{queryBuilder.From.Value.ToString(Constants.DateTimeFormat)}'");
            query.Append($" AND d.\"Timestamp\" <= '{queryBuilder.To.Value.ToString(Constants.DateTimeFormat)}'");
        }

        if (queryBuilder.VariableInListVariables.Any())
        {
            foreach (var variable in queryBuilder.VariableInListVariables)
            {
                query.Append($" AND d.{variable.ToVariableInListString()}");
            }
        }

        if (queryBuilder.HasFieldFilters)
        {
            foreach (var filter in queryBuilder.FieldFilters)
            {
                query.Append($" AND d.{CompileFieldFilter(filter)}");
            }
        }

        // GROUP BY and ORDER BY — no LIMIT needed since generate_series produces exactly Limit bins
        query.Append(" GROUP BY bins.ts ORDER BY bins.ts ASC");

        return query.ToString();
    }

    /// <summary>
    /// Compiles a COUNT query using the same WHERE clause as CompileQuery, without SELECT columns, GROUP BY, ORDER BY, LIMIT, or OFFSET.
    /// </summary>
    public string CompileCountQuery(CrateQueryBuilder queryBuilder)
    {
        var query = new StringBuilder();
        query.Append($"SELECT COUNT(*) FROM {queryBuilder.TenantId}");
        AppendWhereClause(query, queryBuilder);
        return query.ToString();
    }

    private static void AppendWhereClause(StringBuilder query, CrateQueryBuilder queryBuilder)
    {
        if (queryBuilder.VariableInListVariables.Any() || queryBuilder is { From: not null, To: not null } || queryBuilder.CkTypeId != null || queryBuilder.HasFieldFilters)
        {
            // we can only have one where clause, but we can connect it with AND
            query.Append(" WHERE ");
        }

        if(queryBuilder.CkTypeId != null)
        {
            query.Append($"\"CkTypeId\" = '{queryBuilder.CkTypeId.SemanticVersionedFullName}'");

            if (queryBuilder.VariableInListVariables.Any() || queryBuilder is { From: not null, To: not null } || queryBuilder.HasFieldFilters)
            {
                query.Append(" AND ");
            }
        }

        if (queryBuilder.VariableInListVariables.Any())
        {
            query.Append(string.Join(" AND ",
                queryBuilder.VariableInListVariables.Select(x => x.ToVariableInListString())));

            if (queryBuilder is { From: not null, To: not null } || queryBuilder.HasFieldFilters)
            {
                // if we have a time filter as well, we have to connect the filter conditions with an AND
                query.Append(" AND ");
            }
        }

        if (queryBuilder is { From: not null, To: not null })
        {
            query.Append(
                $"\"Timestamp\" >= '{queryBuilder.From.Value.ToString(Constants.DateTimeFormat)}' AND \"Timestamp\" <= '{queryBuilder.To.Value.ToString(Constants.DateTimeFormat)}'");

            if (queryBuilder.HasFieldFilters)
            {
                query.Append(" AND ");
            }
        }

        if (queryBuilder.HasFieldFilters)
        {
            query.Append(string.Join(" AND ",
                queryBuilder.FieldFilters.Select(CompileFieldFilter)));
        }
    }

    private static string CompileFieldFilter(StreamDataFieldFilterDto filter)
    {
        var fieldRef = filter.IsDataField
            ? $"\"data['{filter.FieldName}']\""
            : $"\"{filter.FieldName}\"";

        var op = filter.Operator switch
        {
            StreamDataFieldFilterOperator.Equals => "=",
            StreamDataFieldFilterOperator.NotEquals => "!=",
            StreamDataFieldFilterOperator.GreaterThan => ">",
            StreamDataFieldFilterOperator.GreaterThanOrEqual => ">=",
            StreamDataFieldFilterOperator.LessThan => "<",
            StreamDataFieldFilterOperator.LessThanOrEqual => "<=",
            StreamDataFieldFilterOperator.Like => "LIKE",
            _ => throw new ArgumentOutOfRangeException(nameof(filter), filter.Operator, "Unsupported field filter operator")
        };

        return $"{fieldRef} {op} '{filter.Value}'";
    }
}