using System.Globalization;
using Meshmakers.Octo.Services.Common.StreamData.Dtos;
using Meshmakers.Octo.Services.Common.StreamData.QueryBuilder;

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

        Assert.Equal("SELECT \"data['Voltage']\" FROM meshtest", query);
    }

    [Fact]
    public void IncludeDefaultVariables_ReturnsValidQuery()
    {
        var queryBuilder = new CrateQueryBuilder("meshtest");
        queryBuilder.IncludeDefaultVariables();

        var compiler = new CrateQueryCompiler();
        var query = compiler.CompileQuery(queryBuilder);

        Assert.Equal("SELECT \"Timestamp\", \"RtId\", \"CkTypeId\" FROM meshtest", query);
    }

    [Fact]
    public void IncludeDefaultVariablesAndSingleVariable_ReturnsValidQuery()
    {
        var queryBuilder = new CrateQueryBuilder("meshtest");
        queryBuilder.IncludeDefaultVariables();
        queryBuilder.AddVariable("Voltage", null, null, true);

        var compiler = new CrateQueryCompiler();
        var query = compiler.CompileQuery(queryBuilder);

        Assert.Equal("SELECT \"Timestamp\", \"RtId\", \"CkTypeId\", \"data['Voltage']\" FROM meshtest", query);
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
            "SELECT \"data['Voltage']\" FROM meshtest WHERE \"Timestamp\" >= '2022-01-01 00:00:00.000Z' AND \"Timestamp\" <= '2022-12-31 23:59:59.999Z'",
            query);
    }

    [Fact]
    public void IncludeSingleVariableWithAlias_ReturnsValidQuery()
    {
        var queryBuilder = new CrateQueryBuilder("meshtest");
        queryBuilder.AddVariable("Voltage", "V", null, true);

        var compiler = new CrateQueryCompiler();
        var query = compiler.CompileQuery(queryBuilder);

        Assert.Equal("SELECT \"data['Voltage']\" AS \"V\" FROM meshtest", query);
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
        
        Assert.Equal("SELECT \"Timestamp\", \"RtId\", \"CkTypeId\", \"data['Voltage']\" AS \"V\" FROM meshtest WHERE \"Timestamp\" >= '2022-01-01 00:00:00.000Z' AND \"Timestamp\" <= '2022-12-31 23:59:59.999Z'", query);
    }
    
    [Fact]
    public void IncludeSingleVariableWithAggregationFunctionAndDefaultVariables_ReturnsValidQuery()
    {
        var queryBuilder = new CrateQueryBuilder("meshtest");
        queryBuilder.IncludeDefaultVariables();
        queryBuilder.AddAggregationVariable("Voltage", AggregationFunctionDto.Avg, null, true);
    
        var compiler = new CrateQueryCompiler();
        var query = compiler.CompileQuery(queryBuilder);
    
        Assert.Equal("SELECT \"Timestamp\", \"RtId\", \"CkTypeId\", AVG(data['Voltage']) AS \"Avg_Voltage\" FROM meshtest GROUP BY \"Timestamp\", \"RtId\", \"CkTypeId\"", query);
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
    
        Assert.Equal("SELECT \"Timestamp\", \"RtId\", \"CkTypeId\", AVG(data['Voltage']) AS \"Avg_Voltage\" FROM meshtest GROUP BY \"Timestamp\", \"RtId\", \"CkTypeId\" ORDER BY \"Timestamp\" ASC", query);
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
        
        Assert.Equal("SELECT AVG(data['Voltage']) AS \"V\", \"Timestamp\" AS \"T\" FROM meshtest GROUP BY \"T\" ORDER BY \"T\" ASC", query);
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
        
        Assert.Equal("SELECT AVG(data['Voltage']) AS \"V\", MIN(data['Voltage']) AS \"MinV\", MAX(data['Voltage']) AS \"MaxV\", \"Timestamp\" AS \"T\" FROM meshtest GROUP BY \"T\" ORDER BY \"T\" DESC, \"MaxV\" ASC", query);
    }
}