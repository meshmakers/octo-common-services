using System.Text.Json;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Services.Methods.Handlers;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.Tests.Handlers;

/// <summary>
///     AB#8079: <c>resultJson</c> is the bare camelCase JSON of the declared result (<c>method-wire-contract.md</c>), not the
///     generated <c>{Type}{Method}Result</c> wrapper <c>{"value": ...}</c>. One case per result kind the generator emits.
/// </summary>
public class CkMethodResultWireFormTests
{
    public enum SpecMode
    {
        Unknown = 0,
        Maintenance = 2
    }

    public sealed record SpecRecord
    {
        public string? SpecName { get; init; }

        public double? SpecValue { get; init; }

        public SpecMode? SpecMode { get; init; }
    }

    // shaped like the generated wrappers: one Value of the CLR type CkMethodCodeGenerator maps the result to
    public sealed record StringResult
    {
        public string? Value { get; init; }
    }

    public sealed record IntResult
    {
        public int? Value { get; init; }
    }

    public sealed record BooleanResult
    {
        public bool? Value { get; init; }
    }

    public sealed record EnumResult
    {
        public SpecMode? Value { get; init; }
    }

    public sealed record RecordResult
    {
        public SpecRecord? Value { get; init; }
    }

    public sealed record StringArrayResult
    {
        public IReadOnlyList<string>? Value { get; init; }
    }

    public sealed record NotAWrapper
    {
        public string? ApiKey { get; init; }
    }

    private sealed class ResultHandler<TResult>(TResult result) : ICkMethodHandler<UserChangePasswordParameters, TResult>
        where TResult : class
    {
        public Task<CkMethodOutcome<TResult>> HandleAsync(CkMethodContext<UserChangePasswordParameters> context,
            CancellationToken cancellationToken) => Task.FromResult(CkMethodOutcome<TResult>.Ok(result));
    }

    private static async Task<string?> ResultJsonOf<TResult>(TResult result, AttributeValueTypesDto declared)
        where TResult : class
    {
        var definition = TestData.Definition();
        definition.Result = new CkMethodResultDto { ValueType = declared };
        var host = HostFixture.With(new ResultHandler<TResult>(result), definition);

        var response = await host.Invoker.InvokeAsync(TestData.Request(host.InSeconds(15)),
            TestContext.Current.CancellationToken);

        Assert.True(response.Success, response.Error?.Code);
        return response.ResultJson;
    }

    [Fact]
    public async Task StringResult_IsTheBareJsonString()
    {
        Assert.Equal("\"6ae7c66ccc5a608ccf779df9\"",
            await ResultJsonOf(new StringResult { Value = "6ae7c66ccc5a608ccf779df9" }, AttributeValueTypesDto.String));
    }

    [Fact]
    public async Task IntResult_IsTheBareNumber()
    {
        Assert.Equal("21", await ResultJsonOf(new IntResult { Value = 21 }, AttributeValueTypesDto.Int));
    }

    [Fact]
    public async Task BooleanResult_IsTheBareLiteral()
    {
        Assert.Equal("true", await ResultJsonOf(new BooleanResult { Value = true }, AttributeValueTypesDto.Boolean));
    }

    [Fact]
    public async Task EnumResult_IsTheMemberName()
    {
        Assert.Equal("\"Maintenance\"",
            await ResultJsonOf(new EnumResult { Value = SpecMode.Maintenance }, AttributeValueTypesDto.Enum));
    }

    [Fact]
    public async Task RecordResult_IsTheBareCamelCaseObject()
    {
        var json = await ResultJsonOf(
            new RecordResult { Value = new SpecRecord { SpecName = "Torque", SpecValue = 3.5, SpecMode = SpecMode.Maintenance } },
            AttributeValueTypesDto.Record);

        Assert.Equal("{\"specName\":\"Torque\",\"specValue\":3.5,\"specMode\":\"Maintenance\"}", json);
    }

    [Fact]
    public async Task StringArrayResult_IsTheBareArray()
    {
        Assert.Equal("[\"a\",\"b\"]",
            await ResultJsonOf(new StringArrayResult { Value = ["a", "b"] }, AttributeValueTypesDto.StringArray));
    }

    [Fact]
    public async Task NullValue_HasNoResultJson()
    {
        Assert.Null(await ResultJsonOf(new StringResult { Value = null }, AttributeValueTypesDto.String));
    }

    [Fact]
    public async Task ResultJson_IsNeverTheWrapperObject()
    {
        var json = await ResultJsonOf(new StringResult { Value = "x" }, AttributeValueTypesDto.String);

        using var document = JsonDocument.Parse(json!);
        Assert.Equal(JsonValueKind.String, document.RootElement.ValueKind);
    }

    [Fact]
    public void ResultTypeWithoutValue_FailsAtRegistration_NamingHandlerAndMethod()
    {
        var registrations = new CkMethodHandlerRegistrations();
        registrations.SetServiceName("TestService");

        var e = Assert.Throws<InvalidOperationException>(() =>
            registrations.Add(TestData.MethodId, typeof(ResultHandler<NotAWrapper>)));

        Assert.Contains(TestData.MethodId, e.Message);
        Assert.Contains(nameof(NotAWrapper), e.Message);
        Assert.Contains("Value", e.Message);
    }

    [Fact]
    public void ResultValueOfAnotherType_FailsStartup()
    {
        var definition = TestData.Definition();
        definition.Result = new CkMethodResultDto { ValueType = AttributeValueTypesDto.String };
        var registrations = new CkMethodHandlerRegistrations();
        registrations.SetServiceName("TestService");
        registrations.Add(TestData.MethodId, typeof(ResultHandler<IntResult>));

        var e = Assert.Throws<InvalidOperationException>(() =>
            new CkMethodRegistrationValidator(new FixedDefinitionSource(definition)).Validate(registrations));

        Assert.Contains(TestData.MethodId, e.Message);
        Assert.Contains("result is String", e.Message);
    }
}
