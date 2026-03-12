using Meshmakers.Octo.Services.StreamData;

namespace TimeSeries.Tests;

public class StreamDataFieldResolverTests
{
    [Fact]
    public void DefaultField_PascalCase_ResolvesCorrectly()
    {
        var resolver = new StreamDataFieldResolver();

        var result = resolver.Resolve("RtId");

        Assert.NotNull(result);
        Assert.Equal(StreamDataFieldCategory.Default, result.Category);
        Assert.Equal("RtId", result.CrateDbName);
        Assert.Equal("rtId", result.GraphQlAlias);
        Assert.False(result.IsDataField);
    }

    [Fact]
    public void DefaultField_CamelCase_ResolvesCorrectly()
    {
        var resolver = new StreamDataFieldResolver();

        var result = resolver.Resolve("rtId");

        Assert.NotNull(result);
        Assert.Equal(StreamDataFieldCategory.Default, result.Category);
        Assert.Equal("RtId", result.CrateDbName);
        Assert.Equal("rtId", result.GraphQlAlias);
        Assert.False(result.IsDataField);
    }

    [Fact]
    public void DefaultField_Uppercase_ResolvesCorrectly()
    {
        var resolver = new StreamDataFieldResolver();

        var result = resolver.Resolve("RTID");

        Assert.NotNull(result);
        Assert.Equal(StreamDataFieldCategory.Default, result.Category);
        Assert.Equal("RtId", result.CrateDbName);
        Assert.False(result.IsDataField);
    }

    [Fact]
    public void DefaultField_Lowercase_ResolvesCorrectly()
    {
        var resolver = new StreamDataFieldResolver();

        var result = resolver.Resolve("timestamp");

        Assert.NotNull(result);
        Assert.Equal(StreamDataFieldCategory.Default, result.Category);
        Assert.Equal("Timestamp", result.CrateDbName);
        Assert.Equal("timestamp", result.GraphQlAlias);
        Assert.False(result.IsDataField);
    }

    [Fact]
    public void AllSixDefaultFields_ResolveCorrectly()
    {
        var resolver = new StreamDataFieldResolver();

        var expectedDefaults = new[] { "Timestamp", "RtId", "CkTypeId", "RtWellKnownName", "RtCreationDateTime", "RtChangedDateTime" };

        foreach (var field in expectedDefaults)
        {
            var result = resolver.Resolve(field);
            Assert.NotNull(result);
            Assert.Equal(StreamDataFieldCategory.Default, result.Category);
            Assert.Equal(field, result.CrateDbName);
            Assert.False(result.IsDataField);
        }
    }

    [Fact]
    public void DataStreamField_PascalCase_ResolvesCorrectly()
    {
        var resolver = new StreamDataFieldResolver(["Acknowledged", "Voltage"]);

        var result = resolver.Resolve("Acknowledged");

        Assert.NotNull(result);
        Assert.Equal(StreamDataFieldCategory.DataStream, result.Category);
        Assert.Equal("Acknowledged", result.CrateDbName);
        Assert.Equal("acknowledged", result.GraphQlAlias);
        Assert.True(result.IsDataField);
    }

    [Fact]
    public void DataStreamField_CamelCase_ResolvesCorrectly()
    {
        var resolver = new StreamDataFieldResolver(["Acknowledged"]);

        var result = resolver.Resolve("acknowledged");

        Assert.NotNull(result);
        Assert.Equal(StreamDataFieldCategory.DataStream, result.Category);
        Assert.Equal("Acknowledged", result.CrateDbName);
        Assert.Equal("acknowledged", result.GraphQlAlias);
        Assert.True(result.IsDataField);
    }

    [Fact]
    public void DataStreamField_MixedCase_ResolvesCorrectly()
    {
        var resolver = new StreamDataFieldResolver(["Description"]);

        var result = resolver.Resolve("DESCRIPTION");

        Assert.NotNull(result);
        Assert.Equal(StreamDataFieldCategory.DataStream, result.Category);
        Assert.Equal("Description", result.CrateDbName);
        Assert.True(result.IsDataField);
    }

    [Fact]
    public void UnknownField_Resolve_ReturnsNull()
    {
        var resolver = new StreamDataFieldResolver(["Voltage"]);

        var result = resolver.Resolve("NonExistent");

        Assert.Null(result);
    }

    [Fact]
    public void UnknownField_ResolveOrFallback_ReturnsPascalCased()
    {
        var resolver = new StreamDataFieldResolver(["Voltage"]);

        var result = resolver.ResolveOrFallback("unknownField");

        Assert.Equal(StreamDataFieldCategory.Unknown, result.Category);
        Assert.Equal("UnknownField", result.CrateDbName);
        Assert.Equal("unknownField", result.GraphQlAlias);
        Assert.False(result.IsDataField);
    }

    [Fact]
    public void DefaultField_NeverClassifiedAsDataField()
    {
        // Regression: default fields must never be isDataField=true
        var resolver = new StreamDataFieldResolver(["Voltage"]);

        foreach (var defaultName in new[] { "rtId", "TIMESTAMP", "ckTypeId", "rtWellKnownName", "rtCreationDateTime", "rtChangedDateTime" })
        {
            var result = resolver.Resolve(defaultName);
            Assert.NotNull(result);
            Assert.False(result.IsDataField, $"Default field '{defaultName}' should not be classified as data field");
        }
    }

    [Fact]
    public void DataField_AlwaysClassifiedAsDataField()
    {
        // Regression: data stream fields must always be isDataField=true
        var resolver = new StreamDataFieldResolver(["Acknowledged", "Voltage"]);

        var result1 = resolver.Resolve("acknowledged");
        Assert.NotNull(result1);
        Assert.True(result1.IsDataField, "Data field 'acknowledged' should be classified as data field");

        var result2 = resolver.Resolve("VOLTAGE");
        Assert.NotNull(result2);
        Assert.True(result2.IsDataField, "Data field 'VOLTAGE' should be classified as data field");
    }

    [Fact]
    public void CrateDbName_AlwaysPascalCase()
    {
        // Regression: CrateDbName must be PascalCase regardless of input casing
        var resolver = new StreamDataFieldResolver(["Acknowledged"]);

        Assert.Equal("RtId", resolver.Resolve("rtId")!.CrateDbName);
        Assert.Equal("Timestamp", resolver.Resolve("timestamp")!.CrateDbName);
        Assert.Equal("Acknowledged", resolver.Resolve("acknowledged")!.CrateDbName);
        Assert.Equal("Acknowledged", resolver.Resolve("ACKNOWLEDGED")!.CrateDbName);
    }

    [Fact]
    public void EmptyConstructor_StillResolvesDefaults()
    {
        var resolver = new StreamDataFieldResolver();

        var result = resolver.Resolve("Timestamp");

        Assert.NotNull(result);
        Assert.Equal(StreamDataFieldCategory.Default, result.Category);
    }

    [Fact]
    public void DefaultFieldNameAsDataAttribute_DefaultTakesPrecedence()
    {
        // If a data attribute has the same name as a default, the default wins
        var resolver = new StreamDataFieldResolver(["Timestamp"]);

        var result = resolver.Resolve("Timestamp");

        Assert.NotNull(result);
        Assert.Equal(StreamDataFieldCategory.Default, result.Category);
        Assert.False(result.IsDataField);
    }
}
