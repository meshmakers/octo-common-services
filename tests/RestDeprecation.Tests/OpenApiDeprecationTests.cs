using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace RestDeprecation.Tests;

/// <summary>
///     AB#8421: the Octo OpenAPI document marks exactly the attributed operations deprecated, with sunset date and link,
///     and leaves unmarked operations as they were.
/// </summary>
public sealed class OpenApiDeprecationTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private JsonObject _paths = null!;

    public async ValueTask InitializeAsync()
    {
        _app = await TestApp.StartAsync(
            builder => builder.Services
                .AddOctoApiVersioningAndDocumentation(o => o.AuthorityUrl = "https://identity.example.test/")
                .AddVersion(),
            app => app.MapOpenApi(),
            typeof(UsersTestController), typeof(LegacyTestController));

        using var client = _app.GetTestClient();
        var json = await client.GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        _paths = JsonNode.Parse(json)!["paths"]!.AsObject();
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
    }

    [Theory]
    [InlineData("/{tenantId}/v1/users/{userName}/password-state", "get")]
    [InlineData("/{tenantId}/v1/users/{userName}/reset-password", "post")]
    [InlineData("/{tenantId}/v1/users/{userName}/explode", "get")]
    [InlineData("/{tenantId}/v1/legacy/overridden", "get")]
    public void MarkedOperation_IsDeprecated_WithSunsetAndLink(string path, string method)
    {
        var operation = Operation(path, method);

        Assert.True(operation["deprecated"]!.GetValue<bool>());
        Assert.EndsWith($"Sunset {TestDeprecation.Sunset}, use {TestDeprecation.Link}.",
            operation["description"]!.GetValue<string>());
    }

    [Fact]
    public void MarkedOperation_KeepsItsDescription_AndAppendsTheNotice()
    {
        var operation = Operation("/{tenantId}/v1/users/{userName}/password-state", "get");

        Assert.Equal($"Returns the password state of a user.\n\nSunset {TestDeprecation.Sunset}, use {TestDeprecation.Link}.",
            operation["description"]!.GetValue<string>());
    }

    [Fact]
    public void ControllerAttribute_MarksInheritingOperation()
    {
        var operation = Operation("/{tenantId}/v1/legacy/inherited", "get");

        Assert.True(operation["deprecated"]!.GetValue<bool>());
        Assert.Equal("Sunset 2026-06-01, use https://docs.meshmakers.cloud/legacy.",
            operation["description"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("/{tenantId}/v1/users/{userName}", "get")]
    [InlineData("/{tenantId}/v1/users/{userName}/unmarked-explode", "get")]
    public void UnmarkedOperation_IsNotDeprecated(string path, string method)
    {
        var operation = Operation(path, method);

        Assert.Null(operation["deprecated"]);
        Assert.Null(operation["description"]);
    }

    /// <summary>
    ///     Snapshot of an unmarked operation: the deprecation transformer must leave it byte-identical to what the
    ///     document produced before AB#8421.
    /// </summary>
    [Fact]
    public void UnmarkedOperation_MatchesSnapshot()
    {
        var operation = Operation("/{tenantId}/v1/users/{userName}", "get");

        Assert.Equal(UnmarkedGetUserSnapshot, operation.ToJsonString());
    }

    private JsonObject Operation(string path, string method)
    {
        Assert.True(_paths.ContainsKey(path), $"path {path} missing; document has: {string.Join(", ", _paths.Select(p => p.Key))}");
        return _paths[path]![method]!.AsObject();
    }

    // The operation as Microsoft.AspNetCore.OpenApi + the Octo transformers render it; no "deprecated", no description.
    private const string UnmarkedGetUserSnapshot =
        """
        {"tags":["UsersTest"],"parameters":[{"name":"tenantId","in":"path","required":true,"schema":{"type":"string"}},{"name":"userName","in":"path","required":true,"schema":{"type":"string"}},{"name":"api-version","in":"query","schema":{"type":"string"}}],"responses":{"200":{"description":"OK"}}}
        """;
}
