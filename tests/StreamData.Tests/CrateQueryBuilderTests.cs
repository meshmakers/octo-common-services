using System.Globalization;
using Meshmakers.Octo.Services.StreamData.Dtos;
using Meshmakers.Octo.Services.StreamData.QueryBuilder;

namespace TimeSeries.Tests;

public class CrateQueryBuilderTests
{
    [Fact]
    public void SingleVariable_ReturnsValidQuery()
    {
        var queryBuilder = new CrateQueryBuilder("meshtest");
        queryBuilder.AddVariable("Voltage", null, null, true);

        var compiler = new CrateQueryCompiler();

        var query = compiler.CompileQuery(queryBuilder);

        Assert.Equal("""SELECT "data['Voltage']" FROM meshtest""", query);
    }

    [Fact]
    public void IncludeDefaultVariables_ReturnsValidQuery()
    {
        var queryBuilder = new CrateQueryBuilder("meshtest");
        queryBuilder.IncludeDefaultVariables();

        var compiler = new CrateQueryCompiler();
        var query = compiler.CompileQuery(queryBuilder);

        Assert.Equal("""SELECT "Timestamp", "RtId", "CkTypeId", "RtWellKnownName", "RtCreationDateTime", "RtChangedDateTime" FROM meshtest""", query);
    }

    [Fact]
    public void IncludeDefaultVariablesAndSingleVariable_ReturnsValidQuery()
    {
        var queryBuilder = new CrateQueryBuilder("meshtest");
        queryBuilder.IncludeDefaultVariables();
        queryBuilder.AddVariable("Voltage", null, null, true);

        var compiler = new CrateQueryCompiler();
        var query = compiler.CompileQuery(queryBuilder);

        Assert.Equal("""SELECT "Timestamp", "RtId", "CkTypeId", "RtWellKnownName", "RtCreationDateTime", "RtChangedDateTime", "data['Voltage']" FROM meshtest""", query);
    }

    [Fact]
    public void IncludeSingleVariableAndTimeFilter_ReturnsValidQuery()
    {
        var queryBuilder = new CrateQueryBuilder("meshtest");
        queryBuilder.AddVariable("Voltage", null, null, true);

        var startDate = DateTime.Parse("2022-01-01T00:00Z", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal);
        var endDate = DateTime.Parse("2022-12-31T23:59:59.999Z", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal);
        queryBuilder.WithTimeFilter(startDate, endDate);

        var compiler = new CrateQueryCompiler();
        var query = compiler.CompileQuery(queryBuilder);

        Assert.Equal(
            """SELECT "data['Voltage']" FROM meshtest WHERE "Timestamp" >= '2022-01-01 00:00:00.000Z' AND "Timestamp" <= '2022-12-31 23:59:59.999Z'""",
            query);
    }

    [Fact]
    public void IncludeSingleVariableWithAlias_ReturnsValidQuery()
    {
        var queryBuilder = new CrateQueryBuilder("meshtest");
        queryBuilder.AddVariable("Voltage", "V", null, true);

        var compiler = new CrateQueryCompiler();
        var query = compiler.CompileQuery(queryBuilder);

        Assert.Equal("""SELECT "data['Voltage']" AS "V" FROM meshtest""", query);
    }

    [Fact]
    public void IncludeDefaultVariablesAndSingleVariableWithAliasAndWithTimeFilter_ReturnsValidQuery()
    {
        var queryBuilder = new CrateQueryBuilder("meshtest");
        queryBuilder.IncludeDefaultVariables();
        queryBuilder.AddVariable("Voltage", "V", null, true);

        var startDate = DateTime.Parse("2022-01-01T00:00Z", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal);
        var endDate = DateTime.Parse("2022-12-31T23:59:59.999Z", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal);

        queryBuilder.WithTimeFilter(startDate, endDate);

        var compiler = new CrateQueryCompiler();
        var query = compiler.CompileQuery(queryBuilder);

        Assert.Equal(
            """SELECT "Timestamp", "RtId", "CkTypeId", "RtWellKnownName", "RtCreationDateTime", "RtChangedDateTime", "data['Voltage']" AS "V" FROM meshtest WHERE "Timestamp" >= '2022-01-01 00:00:00.000Z' AND "Timestamp" <= '2022-12-31 23:59:59.999Z'""",
            query);
    }

    [Fact]
    public void IncludeSingleVariableWithAggregationFunctionAndDefaultVariables_ReturnsValidQuery()
    {
        var queryBuilder = new CrateQueryBuilder("meshtest");
        queryBuilder.IncludeDefaultVariables();
        queryBuilder.AddAggregationVariable("Voltage", AggregationFunctionDto.Avg, null, true);

        var compiler = new CrateQueryCompiler();
        var query = compiler.CompileQuery(queryBuilder);

        Assert.Equal(
            """
            SELECT "Timestamp", "RtId", "CkTypeId", "RtWellKnownName", "RtCreationDateTime", "RtChangedDateTime", AVG("data['Voltage']") AS "Avg_Voltage" FROM meshtest GROUP BY "Timestamp", "RtId", "CkTypeId", "RtWellKnownName", "RtCreationDateTime", "RtChangedDateTime"
            """,
            query);
    }

    [Fact]
    public void IncludeSingleVariableWithAggregationFunctionAndDefaultVariablesAndOrderBy_ReturnsValidQuery()
    {
        var queryBuilder = new CrateQueryBuilder("meshtest");
        queryBuilder.IncludeDefaultVariables();
        queryBuilder.AddAggregationVariable("Voltage", AggregationFunctionDto.Avg, null, true);
        queryBuilder.OrderBy("Timestamp", SortOrderDto.Ascending);

        var compiler = new CrateQueryCompiler();
        var query = compiler.CompileQuery(queryBuilder);

        Assert.Equal(
            """SELECT "Timestamp", "RtId", "CkTypeId", "RtWellKnownName", "RtCreationDateTime", "RtChangedDateTime", AVG("data['Voltage']") AS "Avg_Voltage" FROM meshtest GROUP BY "Timestamp", "RtId", "CkTypeId", "RtWellKnownName", "RtCreationDateTime", "RtChangedDateTime" ORDER BY "Timestamp" ASC""",
            query);
    }

    [Fact]
    public void IncludeSingleVariableWithAliasAndAggregationFunction_ReturnsValidQuery()
    {
        var queryBuilder = new CrateQueryBuilder("meshtest");
        queryBuilder.AddAggregationVariable("Voltage", AggregationFunctionDto.Avg, "V", true);
        queryBuilder.AddVariable("Timestamp", "T", null, false);
        queryBuilder.OrderBy("Timestamp", SortOrderDto.Ascending);

        var compiler = new CrateQueryCompiler();
        var query = compiler.CompileQuery(queryBuilder);

        Assert.Equal(
            """SELECT "Timestamp" AS "T", AVG("data['Voltage']") AS "V" FROM meshtest GROUP BY "T" ORDER BY "T" ASC""",
            query);
    }

    [Fact]
    public void IncludeMultipleVariablesWithAliasAggregationFunctions_ReturnsValidQuery()
    {
        var queryBuilder = new CrateQueryBuilder("meshtest");
        queryBuilder.AddAggregationVariable("Voltage", AggregationFunctionDto.Avg, "V", true);
        queryBuilder.AddAggregationVariable("Voltage", AggregationFunctionDto.Min, "MinV", true);
        queryBuilder.AddAggregationVariable("Voltage", AggregationFunctionDto.Max, "MaxV", true);
        queryBuilder.AddVariable("Timestamp", "T", null, false);
        queryBuilder.OrderBy("Timestamp", SortOrderDto.Descending);
        queryBuilder.OrderBy("MaxV", SortOrderDto.Ascending);

        var compiler = new CrateQueryCompiler();
        var query = compiler.CompileQuery(queryBuilder);

        Assert.Equal(
            """SELECT "Timestamp" AS "T", AVG("data['Voltage']") AS "V", MIN("data['Voltage']") AS "MinV", MAX("data['Voltage']") AS "MaxV" FROM meshtest GROUP BY "T" ORDER BY "T" DESC, "MaxV" ASC""",
            query);
    }
    
    [Fact]
    public void IncludeCkTypeId_ReturnsValidQuery()
    {
        var queryBuilder = new CrateQueryBuilder("meshtest");
        queryBuilder.IncludeDefaultVariables();
        queryBuilder.WithCkTypeIdFilter("Test/123");

        var compiler = new CrateQueryCompiler();
        var query = compiler.CompileQuery(queryBuilder);

        Assert.Equal(
            """SELECT "Timestamp", "RtId", "CkTypeId", "RtWellKnownName", "RtCreationDateTime", "RtChangedDateTime" FROM meshtest WHERE "CkTypeId" = 'Test/123'""",
            query);
    }

    [Fact]
    public void SingleFieldFilter_DataVariable_ReturnsValidQuery()
    {
        var queryBuilder = new CrateQueryBuilder("meshtest");
        queryBuilder.AddVariable("Voltage", null, null, true);
        queryBuilder.AddFieldFilter("Voltage", StreamDataFieldFilterOperator.GreaterThan, "220", true);

        var compiler = new CrateQueryCompiler();
        var query = compiler.CompileQuery(queryBuilder);

        Assert.Equal(
            """SELECT "data['Voltage']" FROM meshtest WHERE "data['Voltage']" > '220'""",
            query);
    }

    [Fact]
    public void SingleFieldFilter_NonDataVariable_ReturnsValidQuery()
    {
        var queryBuilder = new CrateQueryBuilder("meshtest");
        queryBuilder.IncludeDefaultVariables();
        queryBuilder.AddFieldFilter("RtId", StreamDataFieldFilterOperator.Equals, "abc123", false);

        var compiler = new CrateQueryCompiler();
        var query = compiler.CompileQuery(queryBuilder);

        Assert.Equal(
            """SELECT "Timestamp", "RtId", "CkTypeId", "RtWellKnownName", "RtCreationDateTime", "RtChangedDateTime" FROM meshtest WHERE "RtId" = 'abc123'""",
            query);
    }

    [Fact]
    public void MultipleFieldFilters_ReturnsValidQuery()
    {
        var queryBuilder = new CrateQueryBuilder("meshtest");
        queryBuilder.AddVariable("Voltage", null, null, true);
        queryBuilder.AddFieldFilter("Voltage", StreamDataFieldFilterOperator.GreaterThanOrEqual, "200", true);
        queryBuilder.AddFieldFilter("Voltage", StreamDataFieldFilterOperator.LessThan, "240", true);

        var compiler = new CrateQueryCompiler();
        var query = compiler.CompileQuery(queryBuilder);

        Assert.Equal(
            """SELECT "data['Voltage']" FROM meshtest WHERE "data['Voltage']" >= '200' AND "data['Voltage']" < '240'""",
            query);
    }

    [Fact]
    public void FieldFilterWithTimeFilter_ReturnsValidQuery()
    {
        var queryBuilder = new CrateQueryBuilder("meshtest");
        queryBuilder.AddVariable("Voltage", null, null, true);
        queryBuilder.AddFieldFilter("Voltage", StreamDataFieldFilterOperator.Equals, "220", true);

        var startDate = DateTime.Parse("2022-01-01T00:00Z", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal);
        var endDate = DateTime.Parse("2022-12-31T23:59:59.999Z", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal);
        queryBuilder.WithTimeFilter(startDate, endDate);

        var compiler = new CrateQueryCompiler();
        var query = compiler.CompileQuery(queryBuilder);

        Assert.Equal(
            """SELECT "data['Voltage']" FROM meshtest WHERE "Timestamp" >= '2022-01-01 00:00:00.000Z' AND "Timestamp" <= '2022-12-31 23:59:59.999Z' AND "data['Voltage']" = '220'""",
            query);
    }

    [Fact]
    public void FieldFilterWithCkTypeIdAndTimeFilter_ReturnsValidQuery()
    {
        var queryBuilder = new CrateQueryBuilder("meshtest");
        queryBuilder.IncludeDefaultVariables();
        queryBuilder.AddVariable("Voltage", null, null, true);
        queryBuilder.WithCkTypeIdFilter("Test/123");
        queryBuilder.AddFieldFilter("Voltage", StreamDataFieldFilterOperator.NotEquals, "0", true);

        var startDate = DateTime.Parse("2022-01-01T00:00Z", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal);
        var endDate = DateTime.Parse("2022-12-31T23:59:59.999Z", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal);
        queryBuilder.WithTimeFilter(startDate, endDate);

        var compiler = new CrateQueryCompiler();
        var query = compiler.CompileQuery(queryBuilder);

        Assert.Equal(
            """SELECT "Timestamp", "RtId", "CkTypeId", "RtWellKnownName", "RtCreationDateTime", "RtChangedDateTime", "data['Voltage']" FROM meshtest WHERE "CkTypeId" = 'Test/123' AND "Timestamp" >= '2022-01-01 00:00:00.000Z' AND "Timestamp" <= '2022-12-31 23:59:59.999Z' AND "data['Voltage']" != '0'""",
            query);
    }

    [Fact]
    public void FieldFilterLikeOperator_ReturnsValidQuery()
    {
        var queryBuilder = new CrateQueryBuilder("meshtest");
        queryBuilder.AddVariable("Status", null, null, true);
        queryBuilder.AddFieldFilter("Status", StreamDataFieldFilterOperator.Like, "%active%", true);

        var compiler = new CrateQueryCompiler();
        var query = compiler.CompileQuery(queryBuilder);

        Assert.Equal(
            """SELECT "data['Status']" FROM meshtest WHERE "data['Status']" LIKE '%active%'""",
            query);
    }

    [Fact]
    public void FieldFilterWithCkTypeIdOnly_ReturnsValidQuery()
    {
        var queryBuilder = new CrateQueryBuilder("meshtest");
        queryBuilder.IncludeDefaultVariables();
        queryBuilder.WithCkTypeIdFilter("Test/123");
        queryBuilder.AddFieldFilter("RtId", StreamDataFieldFilterOperator.Equals, "entity-1", false);

        var compiler = new CrateQueryCompiler();
        var query = compiler.CompileQuery(queryBuilder);

        Assert.Equal(
            """SELECT "Timestamp", "RtId", "CkTypeId", "RtWellKnownName", "RtCreationDateTime", "RtChangedDateTime" FROM meshtest WHERE "CkTypeId" = 'Test/123' AND "RtId" = 'entity-1'""",
            query);
    }

    [Fact]
    public void DataFieldFilter_PascalCaseName_GeneratesCorrectDataSyntax()
    {
        // Regression: data field filter must use data['PascalCase'] not data['camelCase']
        var queryBuilder = new CrateQueryBuilder("meshtest");
        queryBuilder.IncludeDefaultVariables();
        queryBuilder.AddVariable("Acknowledged", "acknowledged", null, true);
        queryBuilder.AddFieldFilter("Acknowledged", StreamDataFieldFilterOperator.Equals, "true", true);

        var compiler = new CrateQueryCompiler();
        var query = compiler.CompileQuery(queryBuilder);

        Assert.Contains("data['Acknowledged']", query);
        Assert.DoesNotContain("data['acknowledged']", query);
    }

    [Fact]
    public void DefaultFieldFilter_GeneratesQuotedPascalCase()
    {
        // Regression: default field filter must use "RtId" not data['rtId']
        var queryBuilder = new CrateQueryBuilder("meshtest");
        queryBuilder.IncludeDefaultVariables();
        queryBuilder.AddFieldFilter("RtId", StreamDataFieldFilterOperator.Equals, "abc123", false);

        var compiler = new CrateQueryCompiler();
        var query = compiler.CompileQuery(queryBuilder);

        Assert.Contains("\"RtId\" = 'abc123'", query);
        Assert.DoesNotContain("data['rtId']", query);
        Assert.DoesNotContain("data['RtId']", query);
    }

    [Fact]
    public void OrderByDefaultField_PascalCase_MatchesAndGeneratesCorrectSql()
    {
        var queryBuilder = new CrateQueryBuilder("meshtest");
        queryBuilder.IncludeDefaultVariables();
        queryBuilder.OrderBy("Timestamp", SortOrderDto.Descending);

        var compiler = new CrateQueryCompiler();
        var query = compiler.CompileQuery(queryBuilder);

        Assert.Contains("""ORDER BY "Timestamp" DESC""", query);
    }

    [Fact]
    public void OrderByDataField_ByCamelCaseAlias_MatchesAndGeneratesCorrectSql()
    {
        var queryBuilder = new CrateQueryBuilder("meshtest");
        queryBuilder.IncludeDefaultVariables();
        queryBuilder.AddVariable("Acknowledged", "acknowledged", null, true);
        queryBuilder.OrderBy("acknowledged", SortOrderDto.Ascending);

        var compiler = new CrateQueryCompiler();
        var query = compiler.CompileQuery(queryBuilder);

        Assert.Contains("""ORDER BY "acknowledged" ASC""", query);
    }

    [Fact]
    public void LimitAndOffset_ReturnsValidQuery()
    {
        var queryBuilder = new CrateQueryBuilder("meshtest");
        queryBuilder.AddVariable("Voltage", null, null, true);
        queryBuilder.WithLimit(10);
        queryBuilder.WithOffset(20);

        var compiler = new CrateQueryCompiler();
        var query = compiler.CompileQuery(queryBuilder);

        Assert.EndsWith("LIMIT 10 OFFSET 20", query);
    }

    [Fact]
    public void OffsetOnly_ReturnsValidQuery()
    {
        var queryBuilder = new CrateQueryBuilder("meshtest");
        queryBuilder.AddVariable("Voltage", null, null, true);
        queryBuilder.WithOffset(5);

        var compiler = new CrateQueryCompiler();
        var query = compiler.CompileQuery(queryBuilder);

        Assert.EndsWith("OFFSET 5", query);
        Assert.DoesNotContain("LIMIT", query);
    }

    [Fact]
    public void CompileCountQuery_Basic_ReturnsValidCountQuery()
    {
        var queryBuilder = new CrateQueryBuilder("meshtest");
        queryBuilder.IncludeDefaultVariables();
        queryBuilder.WithCkTypeIdFilter("Test/123");

        var startDate = DateTime.Parse("2022-01-01T00:00Z", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal);
        var endDate = DateTime.Parse("2022-12-31T23:59:59.999Z", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal);
        queryBuilder.WithTimeFilter(startDate, endDate);

        var compiler = new CrateQueryCompiler();
        var countQuery = compiler.CompileCountQuery(queryBuilder);

        Assert.Equal(
            """SELECT COUNT(*) FROM meshtest WHERE "CkTypeId" = 'Test/123' AND "Timestamp" >= '2022-01-01 00:00:00.000Z' AND "Timestamp" <= '2022-12-31 23:59:59.999Z'""",
            countQuery);
    }

    [Fact]
    public void CompileCountQuery_WithFieldFilters_ReturnsValidCountQuery()
    {
        var queryBuilder = new CrateQueryBuilder("meshtest");
        queryBuilder.IncludeDefaultVariables();
        queryBuilder.AddVariable("Voltage", null, null, true);
        queryBuilder.AddFieldFilter("Voltage", StreamDataFieldFilterOperator.GreaterThan, "220", true);

        var compiler = new CrateQueryCompiler();
        var countQuery = compiler.CompileCountQuery(queryBuilder);

        Assert.Equal(
            """SELECT COUNT(*) FROM meshtest WHERE "data['Voltage']" > '220'""",
            countQuery);
        Assert.DoesNotContain("ORDER BY", countQuery);
        Assert.DoesNotContain("LIMIT", countQuery);
        Assert.DoesNotContain("OFFSET", countQuery);
    }

    [Fact]
    public void CompileCountQuery_WithWhereIn_ReturnsValidCountQuery()
    {
        var queryBuilder = new CrateQueryBuilder("meshtest");
        queryBuilder.IncludeDefaultVariables();
        queryBuilder.WithCkTypeIdFilter("Test/123");
        queryBuilder.AddWhereIn("RtId", ["id1", "id2"]);

        var compiler = new CrateQueryCompiler();
        var countQuery = compiler.CompileCountQuery(queryBuilder);

        Assert.StartsWith("SELECT COUNT(*) FROM meshtest WHERE", countQuery);
        Assert.Contains("\"RtId\" IN ('id1', 'id2')", countQuery);
    }

    [Fact]
    public void CompileCountQuery_NoGroupByOrOrderBy_ReturnsValidCountQuery()
    {
        var queryBuilder = new CrateQueryBuilder("meshtest");
        queryBuilder.IncludeDefaultVariables();
        queryBuilder.AddAggregationVariable("Voltage", AggregationFunctionDto.Avg, null, true);
        queryBuilder.OrderBy("Timestamp", SortOrderDto.Ascending);
        queryBuilder.WithLimit(100);
        queryBuilder.WithOffset(50);

        var compiler = new CrateQueryCompiler();
        var countQuery = compiler.CompileCountQuery(queryBuilder);

        Assert.StartsWith("SELECT COUNT(*) FROM meshtest", countQuery);
        Assert.DoesNotContain("GROUP BY", countQuery);
        Assert.DoesNotContain("ORDER BY", countQuery);
        Assert.DoesNotContain("LIMIT", countQuery);
        Assert.DoesNotContain("OFFSET", countQuery);
    }

    [Fact]
    public void AddOrderByTiebreaker_AddsTimestampWhenNotPresent()
    {
        var queryBuilder = new CrateQueryBuilder("meshtest");
        queryBuilder.IncludeDefaultVariables();
        queryBuilder.AddVariable("Received", "received", null, true);
        queryBuilder.OrderBy("received", SortOrderDto.Descending);
        queryBuilder.AddOrderByTiebreaker("Timestamp", SortOrderDto.Ascending);

        var compiler = new CrateQueryCompiler();
        var query = compiler.CompileQuery(queryBuilder);

        Assert.Contains("""ORDER BY "received" DESC, "Timestamp" ASC""", query);
    }

    [Fact]
    public void AddOrderByTiebreaker_NoOpWhenTimestampAlreadyInOrderBy()
    {
        var queryBuilder = new CrateQueryBuilder("meshtest");
        queryBuilder.IncludeDefaultVariables();
        queryBuilder.OrderBy("Timestamp", SortOrderDto.Descending);
        queryBuilder.AddOrderByTiebreaker("Timestamp", SortOrderDto.Ascending);

        var compiler = new CrateQueryCompiler();
        var query = compiler.CompileQuery(queryBuilder);

        // Should NOT have Timestamp twice — tiebreaker is skipped
        Assert.Contains("""ORDER BY "Timestamp" DESC""", query);
        Assert.DoesNotContain("ASC", query);
    }

    [Fact]
    public void AddOrderByTiebreaker_NoOpWhenNoExistingOrderBy()
    {
        var queryBuilder = new CrateQueryBuilder("meshtest");
        queryBuilder.IncludeDefaultVariables();
        queryBuilder.AddOrderByTiebreaker("Timestamp", SortOrderDto.Ascending);

        var compiler = new CrateQueryCompiler();
        var query = compiler.CompileQuery(queryBuilder);

        Assert.DoesNotContain("ORDER BY", query);
    }

    [Fact]
    public void DownsamplingWithAggregation_SingleColumn_EmitsDateBinGroupByAndOrderBy()
    {
        var from = DateTime.Parse("2024-01-01T00:00Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        var to = DateTime.Parse("2024-01-01T01:00Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        // 3600 seconds / 10 bins = 360 seconds per bin
        var queryBuilder = new CrateQueryBuilder("meshtest");
        queryBuilder.WithCkTypeIdFilter("Test/123");
        queryBuilder.WithDownsampling(10, from, to);
        queryBuilder.AddVariable("Timestamp", "T", null, false);
        queryBuilder.AddAggregationVariable("Voltage", AggregationFunctionDto.Avg, "Avg_Voltage", true);

        var compiler = new CrateQueryCompiler();
        var sql = compiler.CompileQuery(queryBuilder);

        Assert.Contains("DATE_BIN('360 seconds'::INTERVAL, \"Timestamp\", 0) AS \"T\"", sql);
        Assert.Contains("AVG(\"data['Voltage']\") AS \"Avg_Voltage\"", sql);
        Assert.Contains("GROUP BY DATE_BIN('360 seconds'::INTERVAL, \"Timestamp\", 0)", sql);
        Assert.Contains("ORDER BY \"T\" ASC", sql);
        Assert.Contains("LIMIT 10", sql);
    }

    [Fact]
    public void DownsamplingWithAggregation_MultipleColumns_EmitsAllAggregates()
    {
        var from = DateTime.Parse("2024-01-01T00:00Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        var to = DateTime.Parse("2024-01-01T00:50Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        // 3000 seconds / 5 bins = 600 seconds per bin
        var queryBuilder = new CrateQueryBuilder("meshtest");
        queryBuilder.WithCkTypeIdFilter("Energy/Meter");
        queryBuilder.WithDownsampling(5, from, to);
        queryBuilder.AddVariable("Timestamp", "T", null, false);
        queryBuilder.AddAggregationVariable("Power", AggregationFunctionDto.Avg, "Avg_Power", true);
        queryBuilder.AddAggregationVariable("Power", AggregationFunctionDto.Min, "Min_Power", true);
        queryBuilder.AddAggregationVariable("Power", AggregationFunctionDto.Max, "Max_Power", true);

        var compiler = new CrateQueryCompiler();
        var sql = compiler.CompileQuery(queryBuilder);

        Assert.Contains("DATE_BIN('600 seconds'::INTERVAL, \"Timestamp\", 0) AS \"T\"", sql);
        Assert.Contains("AVG(\"data['Power']\") AS \"Avg_Power\"", sql);
        Assert.Contains("MIN(\"data['Power']\") AS \"Min_Power\"", sql);
        Assert.Contains("MAX(\"data['Power']\") AS \"Max_Power\"", sql);
        Assert.Contains("GROUP BY DATE_BIN('600 seconds'::INTERVAL, \"Timestamp\", 0)", sql);
        Assert.Contains("ORDER BY \"T\" ASC", sql);
    }

    [Fact]
    public void DownsamplingWithoutAggregation_NoGroupByOrOrderBy()
    {
        var from = DateTime.Parse("2024-01-01T00:00Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        var to = DateTime.Parse("2024-01-01T01:00Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        var queryBuilder = new CrateQueryBuilder("meshtest");
        queryBuilder.WithCkTypeIdFilter("Test/123");
        queryBuilder.WithDownsampling(10, from, to);
        queryBuilder.AddVariable("Timestamp", "T", null, false);
        queryBuilder.AddVariable("Voltage", null, null, true);

        var compiler = new CrateQueryCompiler();
        var sql = compiler.CompileQuery(queryBuilder);

        Assert.Contains("DATE_BIN('360 seconds'::INTERVAL", sql);
        Assert.DoesNotContain("GROUP BY", sql);
        Assert.DoesNotContain("ORDER BY", sql);
    }

    [Fact]
    public void DownsamplingWithAggregation_AndFieldFilter_EmitsWhereAndGroupBy()
    {
        var from = DateTime.Parse("2024-01-01T00:00Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        var to = DateTime.Parse("2024-01-01T01:00Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        var queryBuilder = new CrateQueryBuilder("meshtest");
        queryBuilder.WithCkTypeIdFilter("Test/123");
        queryBuilder.WithDownsampling(6, from, to);
        queryBuilder.AddVariable("Timestamp", "T", null, false);
        queryBuilder.AddAggregationVariable("Voltage", AggregationFunctionDto.Max, "Max_Voltage", true);
        queryBuilder.AddFieldFilter("Voltage", StreamDataFieldFilterOperator.GreaterThan, "0", true);

        var compiler = new CrateQueryCompiler();
        var sql = compiler.CompileQuery(queryBuilder);

        Assert.Contains("GROUP BY DATE_BIN('600 seconds'::INTERVAL, \"Timestamp\", 0)", sql);
        Assert.Contains("ORDER BY \"T\" ASC", sql);
        Assert.Contains("\"data['Voltage']\" > '0'", sql);
    }
}