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
}