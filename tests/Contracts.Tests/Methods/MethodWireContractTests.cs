using System.Text.Json;
using System.Text.Json.Nodes;
using MassTransit.Serialization;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Xunit;

namespace Contracts.Tests.Methods;

/// <summary>
///     Pins the wire format of the CK method contract (AB#6874). The golden files in <c>Golden/</c> are what the
///     event hub serializer writes; a change of the serialized form fails these tests and must be a conscious, additive
///     decision (see <c>docs/method-wire-contract.md</c>, "Rules of evolution"). To refresh the files after an additive
///     change, set the environment variable <c>OCTO_UPDATE_GOLDEN=1</c> and run the tests from the source folder.
/// </summary>
public class MethodWireContractTests
{
    private static readonly JsonSerializerOptions Options = SystemTextJsonMessageSerializer.Options;

    public static TheoryData<string> GoldenNames => new()
    {
        "request", "response-success", "response-error", "response-accepted", "handler-announced",
        "handler-withdrawn", "handler-discovery-requested"
    };

    private static object Sample(string name) => name switch
    {
        "request" => MethodContractSamples.Request(),
        "response-success" => MethodContractSamples.Success(),
        "response-error" => MethodContractSamples.Error(),
        "response-accepted" => MethodContractSamples.Accepted(),
        "handler-announced" => MethodContractSamples.Announced(),
        "handler-withdrawn" => MethodContractSamples.Withdrawn(),
        "handler-discovery-requested" => MethodContractSamples.Discovery(),
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };

    [Theory]
    [MemberData(nameof(GoldenNames))]
    public void Serialized_Matches_GoldenFile(string name)
    {
        var actual = JsonSerializer.SerializeToNode(Sample(name), Sample(name).GetType(), Options)!;
        var path = Path.Combine(AppContext.BaseDirectory, "Golden", $"{name}.json");

        if (Environment.GetEnvironmentVariable("OCTO_UPDATE_GOLDEN") == "1")
        {
            var source = Path.Combine(FindRepoRoot(), "tests", "Contracts.Tests", "Methods", "Golden", $"{name}.json");
            File.WriteAllText(source,
                actual.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
            return;
        }

        var golden = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.True(JsonNode.DeepEquals(golden, actual),
            $"Wire format of '{name}' changed.{Environment.NewLine}Golden:{Environment.NewLine}{golden.ToJsonString()}{Environment.NewLine}Actual:{Environment.NewLine}{actual.ToJsonString()}");
    }

    [Theory]
    [MemberData(nameof(GoldenNames))]
    public void GoldenFile_Deserializes_AndRoundTrips(string name)
    {
        var type = Sample(name).GetType();
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", $"{name}.json"));

        var message = JsonSerializer.Deserialize(text, type, Options);
        var again = JsonSerializer.SerializeToNode(message, type, Options);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(text), again));
    }

    [Theory]
    [MemberData(nameof(GoldenNames))]
    public void UnknownMembers_AreIgnored_ForRollingUpdates(string name)
    {
        var type = Sample(name).GetType();
        var golden = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", $"{name}.json"));
        var node = JsonNode.Parse(golden)!.AsObject();
        node["addedByANewerVersion"] = "x";
        node["nested"] = new JsonObject { ["a"] = 1 };

        var message = JsonSerializer.Deserialize(node.ToJsonString(), type, Options);

        Assert.NotNull(message);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(golden), JsonSerializer.SerializeToNode(message, type, Options)));
    }

    [Fact]
    public void Request_WithUnknownMember_KeepsItsKnownMembers()
    {
        var node = JsonSerializer.SerializeToNode(MethodContractSamples.Request(), Options)!.AsObject();
        node["futureMember"] = new JsonArray(1, 2);

        var request = JsonSerializer.Deserialize<CkMethodRequest>(node.ToJsonString(), Options)!;

        Assert.Equal("meshtest", request.TenantId);
        Assert.Equal(MethodContractSamples.InvocationId, request.InvocationId);
        Assert.Equal("key-1", request.IdempotencyKey);
    }

    [Fact]
    public void MissingContractVersion_ReadsAsOne()
    {
        var request = JsonSerializer.SerializeToNode(MethodContractSamples.Request(), Options)!.AsObject();
        request.Remove("contractVersion");
        var response = JsonSerializer.SerializeToNode(MethodContractSamples.Success(), Options)!.AsObject();
        response.Remove("contractVersion");
        var announced = JsonSerializer.SerializeToNode(MethodContractSamples.Announced(), Options)!.AsObject();
        announced.Remove("contractVersion");

        Assert.Equal(1, JsonSerializer.Deserialize<CkMethodRequest>(request.ToJsonString(), Options)!.ContractVersion);
        Assert.Equal(1, JsonSerializer.Deserialize<CkMethodResponse>(response.ToJsonString(), Options)!.ContractVersion);
        Assert.Equal(1,
            JsonSerializer.Deserialize<CkMethodHandlerAnnounced>(announced.ToJsonString(), Options)!.ContractVersion);
    }

    [Fact]
    public void CurrentVersion_IsWrittenOnRequestResponseAndAnnouncement()
    {
        Assert.Equal(CkMethodContract.CurrentVersion, MethodContractSamples.Request().ContractVersion);
        Assert.Equal(CkMethodContract.CurrentVersion, MethodContractSamples.Success().ContractVersion);
        Assert.Equal(CkMethodContract.CurrentVersion, MethodContractSamples.Announced().ContractVersion);
        Assert.Equal(1, CkMethodContract.CurrentVersion);
    }

    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(2, 1, false)]
    [InlineData(1, 2, true)]
    [InlineData(0, 1, false)]
    public void IsSupported_AcceptsOlderAndEqualVersionsOnly(int message, int supported, bool expected) =>
        Assert.Equal(expected, CkMethodContract.IsSupported(message, supported));

    [Fact]
    public void ErrorResponse_NeedsNoLocalizedText_AndHasNoMessageMember()
    {
        var json = JsonSerializer.SerializeToNode(MethodContractSamples.Error(), Options)!.AsObject();

        Assert.False(json.ContainsKey("message"));
        Assert.False(json["error"]!.AsObject().ContainsKey("message"));
        Assert.Equal("PASSWORD_TOO_SHORT", json["error"]!["code"]!.GetValue<string>());
        Assert.Equal("8", json["error"]!["parameters"]!["minLength"]!.GetValue<string>());
    }

    [Fact]
    public void ResponseShapes_AreDistinguishable()
    {
        var ok = MethodContractSamples.Success();
        var failed = MethodContractSamples.Error();
        var accepted = MethodContractSamples.Accepted();

        Assert.True(ok.Success);
        Assert.False(ok.IsAccepted);
        Assert.Null(ok.Error);
        Assert.False(failed.Success);
        Assert.NotNull(failed.Error);
        Assert.True(accepted.Success);
        Assert.True(accepted.IsAccepted);
        Assert.Null(accepted.ResultJson);
        var json = JsonSerializer.SerializeToNode(accepted, Options)!.AsObject();
        Assert.False(json.ContainsKey("isAccepted"));
    }

    [Fact]
    public void Request_ToString_RedactsSecrets()
    {
        var request = MethodContractSamples.Request() with
        {
            ParametersJson = "{\"newPassword\":\"S3cret!\"}", CallerAccessToken = "eyJhbGciOi.secret.token"
        };

        var text = request.ToString();

        Assert.Contains("System.Identity/User.ChangePassword-1", text);
        Assert.Contains("meshtest", text);
        Assert.Contains(request.InvocationId.ToString(), text);
        Assert.DoesNotContain("S3cret", text);
        Assert.DoesNotContain("eyJ", text);
        Assert.DoesNotContain("Alice", text);
        Assert.DoesNotContain("key-1", text);
        Assert.Equal(text, $"{request}");
    }

    [Fact]
    public void Request_IsSealed_SoTheRedactingToStringCannotBeBypassed() =>
        Assert.True(typeof(CkMethodRequest).IsSealed);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Octo.Common.Services.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
