using System.Text.Json;
using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Services.Methods.Handlers;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.Tests.Typed;

/// <summary>
///     AB#8707: the generated parameter records of a model bind as handler parameter types with <see cref="CkMethodJson.Options" />,
///     also when a Record parameter holds a record array (generated as the interface <c>IAttributeValueList&lt;T&gt;</c>).
/// </summary>
public class CkMethodJsonRuntimeRecordTests
{
    private const string Target = "Industry.Energy/Meter";

    [Fact]
    public void Deserialize_RecordParameterWithRecordArray_BindsTheElements()
    {
        const string json =
            "{\"archive\":{\"targetCkTypeId\":\"" + Target + "\",\"columns\":[{\"path\":\"Voltage\",\"indexed\":true,\"required\":false}]}}";

        var parameters = JsonSerializer.Deserialize<TimeRangeArchiveCreateArchiveParameters>(json, CkMethodJson.Options)!;

        Assert.Equal(Target, parameters.Archive.TargetCkTypeId);
        var column = Assert.Single(parameters.Archive.Columns!);
        Assert.Equal("Voltage", column.Path);
        Assert.True(column.Indexed);
        Assert.False(column.Required);
    }

    [Fact]
    public void Deserialize_EmptyRecordArray_IsAnEmptyList()
    {
        const string json = "{\"archive\":{\"targetCkTypeId\":\"" + Target + "\",\"columns\":[]}}";

        var parameters = JsonSerializer.Deserialize<TimeRangeArchiveCreateArchiveParameters>(json, CkMethodJson.Options)!;

        Assert.NotNull(parameters.Archive.Columns);
        Assert.Empty(parameters.Archive.Columns!);
    }

    [Fact]
    public void Deserialize_MissingRecordArray_IsEmpty()
    {
        const string json = "{\"archive\":{\"targetCkTypeId\":\"" + Target + "\"}}";

        var parameters = JsonSerializer.Deserialize<TimeRangeArchiveCreateArchiveParameters>(json, CkMethodJson.Options)!;

        // columns is a mandatory attribute of the CK record: the generated getter reads a missing value as an empty list
        Assert.Equal(Target, parameters.Archive.TargetCkTypeId);
        Assert.Empty(parameters.Archive.Columns!);
    }

    [Fact]
    public void Deserialize_NullRecordArray_IsEmpty()
    {
        const string json = "{\"archive\":{\"targetCkTypeId\":\"" + Target + "\",\"columns\":null}}";

        var parameters = JsonSerializer.Deserialize<TimeRangeArchiveCreateArchiveParameters>(json, CkMethodJson.Options)!;

        Assert.Empty(parameters.Archive.Columns!);
    }

    [Fact]
    public void Deserialize_SeveralRecordArraysAndEnums_BindAll()
    {
        const string json = "{\"archive\":{\"sources\":[{\"sourceArchiveRtId\":\"65a000000000000000000001\"}," +
                            "{\"sourceArchiveRtId\":\"65a000000000000000000002\"}],\"bucketSizeMs\":60000,\"watermarkLagMs\":0," +
                            "\"aggregations\":[{\"sourcePath\":\"Voltage\",\"function\":\"Avg\"}]}}";

        var parameters = JsonSerializer.Deserialize<RollupArchiveCreateArchiveParameters>(json, CkMethodJson.Options)!;

        Assert.Equal(["65a000000000000000000001", "65a000000000000000000002"],
            parameters.Archive.Sources!.Select(s => s.SourceArchiveRtId).ToArray());
        var aggregation = Assert.Single(parameters.Archive.Aggregations!);
        Assert.Equal("Voltage", aggregation.SourcePath);
        Assert.Equal("Avg", aggregation.Function.ToString());
        Assert.Equal(60000, parameters.Archive.BucketSizeMs);
    }

    [Fact]
    public void Serialize_WritesTheCkAttributesOnly_AndRoundTrips()
    {
        const string json =
            "{\"archive\":{\"targetCkTypeId\":\"" + Target + "\",\"columns\":[{\"path\":\"Voltage\",\"indexed\":true,\"required\":false}," +
            "{\"path\":\"Current\",\"indexed\":false,\"required\":true}]}}";
        var parameters = JsonSerializer.Deserialize<TimeRangeArchiveCreateArchiveParameters>(json, CkMethodJson.Options)!;

        var written = JsonSerializer.Serialize(parameters, CkMethodJson.Options);
        var again = JsonSerializer.Deserialize<TimeRangeArchiveCreateArchiveParameters>(written, CkMethodJson.Options)!;

        Assert.DoesNotContain("\"attributes\"", written);
        Assert.DoesNotContain("\"ckRecordId\"", written);
        Assert.Contains("\"columns\":[{", written);
        Assert.Equal(parameters.Archive.TargetCkTypeId, again.Archive.TargetCkTypeId);
        Assert.Equal(
            parameters.Archive.Columns!.Select(c => (c.Path, c.Indexed, c.Required)),
            again.Archive.Columns!.Select(c => (c.Path, c.Indexed, c.Required)));
        Assert.Equal(written, JsonSerializer.Serialize(again, CkMethodJson.Options));
    }

    [Fact]
    public void Registration_DeserializeParameters_BindsTheGeneratedRecord()
    {
        var registration = CkMethodRegistration.Create(SystemStreamDataCkIds.TimeRangeArchiveCreateArchiveMethodId,
            typeof(CreateArchiveHandler));

        var parameters = (TimeRangeArchiveCreateArchiveParameters)registration.DeserializeParameters(
            "{\"archive\":{\"targetCkTypeId\":\"" + Target + "\",\"columns\":[{\"path\":\"Voltage\",\"indexed\":true}]}}");

        Assert.Equal("Voltage", Assert.Single(parameters.Archive.Columns!).Path);
    }

    [Fact]
    public void Registration_DeserializeParameters_WrongElementShape_IsAValidationFailureOnTheField()
    {
        var registration = CkMethodRegistration.Create(SystemStreamDataCkIds.TimeRangeArchiveCreateArchiveMethodId,
            typeof(CreateArchiveHandler));

        var e = Assert.Throws<CkMethodParameterException>(() => registration.DeserializeParameters(
            "{\"archive\":{\"targetCkTypeId\":\"" + Target + "\",\"columns\":[42]}}"));

        Assert.Equal("archive.columns[0]", e.Field);
        Assert.Equal("invalid-value", e.Reason);
    }

    [Theory]
    [InlineData("[null]")]
    [InlineData("[{\"path\":\"Voltage\"},null]")]
    public void Registration_DeserializeParameters_ANullElement_IsAValidationFailure(string columns)
    {
        var registration = CkMethodRegistration.Create(SystemStreamDataCkIds.TimeRangeArchiveCreateArchiveMethodId,
            typeof(CreateArchiveHandler));

        var e = Assert.Throws<CkMethodParameterException>(() => registration.DeserializeParameters(
            "{\"archive\":{\"targetCkTypeId\":\"" + Target + "\",\"columns\":" + columns + "}}"));

        Assert.Equal("invalid-value", e.Reason);
        Assert.StartsWith("archive", e.Field);
    }

    private sealed class CreateArchiveHandler
        : ICkMethodHandler<TimeRangeArchiveCreateArchiveParameters, TimeRangeArchiveCreateArchiveResult>
    {
        public Task<CkMethodOutcome<TimeRangeArchiveCreateArchiveResult>> HandleAsync(
            CkMethodContext<TimeRangeArchiveCreateArchiveParameters> context, CancellationToken cancellationToken) =>
            Task.FromResult<CkMethodOutcome<TimeRangeArchiveCreateArchiveResult>>(
                new TimeRangeArchiveCreateArchiveResult { Value = "x" });
    }
}
