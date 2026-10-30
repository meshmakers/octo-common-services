using System.Text.Json;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods.Idempotency;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.Tests.InProcess;

/// <summary>AB#7819: the keyed parameters fingerprint, computed from the raw request before redaction.</summary>
public class ParametersFingerprintTests
{
    // synthetic key material, never a real secret
    private static readonly string KeyOne = Convert.ToBase64String(Enumerable.Repeat((byte)0x11, 32).ToArray());
    private static readonly string KeyTwo = Convert.ToBase64String(Enumerable.Repeat((byte)0x22, 32).ToArray());

    private static CkMethodFingerprintOptions Keys(string? current = null, string? previous = null,
        string currentId = "k-current", string previousId = "k-previous") =>
        new()
        {
            Current = current == null ? new() : new() { Id = currentId, Secret = current },
            Previous = previous == null ? new() : new() { Id = previousId, Secret = previous }
        };

    private static CkMethodParametersFingerprinter Fingerprinter(CkMethodFingerprintOptions options) =>
        new(Options.Create(options));

    private static CkMethodRequest Keyed(string? parametersJson = null, string target = "65f0c0ffee",
        string type = "System.Identity/User") =>
        TestData.Request(DateTime.UtcNow.AddSeconds(30), parametersJson: parametersJson) with
        {
            IdempotencyKey = "key-1", TargetRtId = target, CkTypeId = type
        };

    private static CkMethodParametersFingerprint Primary(CkMethodParametersFingerprinter f, CkMethodRequest r,
        bool sensitive = true) => f.Compute(r, sensitive).Primary!;

    // ---------------------------------------------------------------- what the fingerprint covers

    [Fact]
    public void SameParametersInAnotherPropertyOrder_GiveTheSameFingerprint()
    {
        var f = Fingerprinter(Keys(KeyOne));
        var a = Keyed("{\"currentPassword\":\"a\",\"newPassword\":\"b\",\"nested\":{\"x\":1,\"y\":[1,2]}}");
        var b = Keyed("{ \"nested\": {\"y\":[1,2], \"x\":1}, \"newPassword\":\"b\", \"currentPassword\":\"a\" }");

        Assert.True(Primary(f, a).Matches(Primary(f, b)));
    }

    [Theory]
    [InlineData("{\"v\":1}", "{\"v\":1.0}")]
    [InlineData("{\"v\":1}", "{\"v\":1e0}")]
    [InlineData("{\"v\":\"A\"}", "{\"v\":\"\\u0041\"}")]
    [InlineData("{\"v\":1,\"v\":2}", "{\"v\":2}")]
    [InlineData("", "{}")]
    public void EqualValues_InDifferentSpellings_GiveTheSameFingerprint(string? left, string right)
    {
        var f = Fingerprinter(Keys(KeyOne));

        Assert.True(Primary(f, Keyed(left)).Matches(Primary(f, Keyed(right))));
    }

    [Theory]
    [InlineData("{\"v\":1}", "{\"v\":2}")]
    [InlineData("{\"v\":[1,2]}", "{\"v\":[2,1]}")]
    [InlineData("{\"v\":\"1\"}", "{\"v\":1}")]
    [InlineData("{\"v\":null}", "{}")]
    public void DifferentValues_GiveDifferentFingerprints(string left, string right)
    {
        var f = Fingerprinter(Keys(KeyOne));

        Assert.False(Primary(f, Keyed(left)).Matches(Primary(f, Keyed(right))));
    }

    [Fact]
    public void ChangedSensitiveValue_TwoDifferentNewPasswords_GiveDifferentFingerprints()
    {
        var f = Fingerprinter(Keys(KeyOne));
        var first = Primary(f, Keyed("{\"currentPassword\":\"old\",\"newPassword\":\"Pa55-one\"}"));
        var second = Primary(f, Keyed("{\"currentPassword\":\"old\",\"newPassword\":\"Pa55-two\"}"));

        Assert.False(first.Matches(second));
    }

    [Fact]
    public void ChangedTargetOrType_GivesADifferentFingerprint()
    {
        var f = Fingerprinter(Keys(KeyOne));
        var baseline = Primary(f, Keyed());

        Assert.False(baseline.Matches(Primary(f, Keyed(target: "65f0c0ffef"))));
        Assert.False(baseline.Matches(Primary(f, Keyed(type: "System.Identity/Client"))));
    }

    [Fact]
    public void TheSameFingerprint_IsStableAcrossInstances_AsAllReplicasShareTheKey()
    {
        var one = Primary(Fingerprinter(Keys(KeyOne)), Keyed());
        var two = Primary(Fingerprinter(Keys(KeyOne)), Keyed());

        Assert.True(one.Matches(two));
        Assert.Equal(one.ToArray(), two.ToArray());
    }

    [Fact]
    public void AnotherKey_GivesAnotherFingerprint_AndKeepsTheKeyId()
    {
        var a = Primary(Fingerprinter(Keys(KeyOne)), Keyed());
        var b = Primary(Fingerprinter(Keys(KeyTwo)), Keyed());

        Assert.NotEqual(a.ToArray(), b.ToArray());
        Assert.Equal("k-current", a.KeyId);
    }

    // ---------------------------------------------------------------- rotation

    [Fact]
    public async Task AfterRotation_AFingerprintOfTheOldKeyStillVerifies_AndAChangedRequestDoesNot()
    {
        var stored = Primary(Fingerprinter(Keys(KeyOne, currentId: "k-old")), Keyed()); // what the store holds
        var rotated = Fingerprinter(Keys(KeyTwo, KeyOne, "k-new", "k-old"));

        var same = await FactsOf(rotated, Keyed());
        var changed = await FactsOf(rotated, Keyed("{\"newPassword\":\"other\"}"));

        Assert.Equal("k-old", stored.KeyId);
        Assert.Equal("k-new", same.ParametersFingerprint!.KeyId);
        Assert.True(same.MatchesStoredFingerprint(stored));
        Assert.False(changed.MatchesStoredFingerprint(stored));
    }

    [Fact]
    public async Task AFingerprintOfAnUnknownKeyId_NeverVerifies()
    {
        var stored = Primary(Fingerprinter(Keys(KeyOne, currentId: "k-gone")), Keyed());
        var facts = await FactsOf(Fingerprinter(Keys(KeyTwo)), Keyed());

        Assert.False(facts.MatchesStoredFingerprint(stored));
        Assert.False(facts.MatchesStoredFingerprint(null));
    }

    [Fact]
    public async Task UnkeyedSha256OfAMethodWithoutSensitiveParameters_StillVerifiesAfterAKeyWasConfigured()
    {
        var request = Keyed("{\"hint\":\"x\"}");
        var stored = Primary(Fingerprinter(Keys()), request, false);
        var facts = await FactsOf(Fingerprinter(Keys(KeyOne)), request, TestData.DefinitionWithoutSensitive());

        Assert.Equal(CkMethodParametersFingerprinter.UnkeyedKeyId, stored.KeyId);
        Assert.True(facts.MatchesStoredFingerprint(stored));
    }

    // ---------------------------------------------------------------- no key configured

    [Fact]
    public async Task NoKey_KeyedCall_MethodWithSensitiveParameters_IsRefused_AndTheHandlerDoesNotRun()
    {
        var transportCalls = 0;
        var inner = new Probe(CkMethodInvocationFilterOrder.Idempotency);
        var response = await Run(Fingerprinter(Keys()), Keyed(), TestData.Definition(), () => transportCalls++, inner);

        Assert.False(response.Success);
        Assert.Equal(CkMethodErrorCodes.MethodValidationFailed, response.Error!.Code);
        Assert.Equal("idempotencyKey", response.Error.Parameters![CkMethodErrorParameters.Field]);
        Assert.Equal("fingerprint-key-missing", response.Error.Parameters[CkMethodErrorParameters.Reason]);
        Assert.Equal(0, transportCalls);
        Assert.Equal(0, inner.Calls); // the idempotency filter never sees the call
    }

    [Fact]
    public async Task NoKey_KeyedCall_ToAnUnknownMethod_IsRefused_FailClosed()
    {
        var transportCalls = 0;
        var response = await Run(Fingerprinter(Keys()), Keyed(), null, () => transportCalls++);

        Assert.Equal(CkMethodErrorCodes.MethodValidationFailed, response.Error!.Code);
        Assert.Equal(0, transportCalls);
    }

    [Fact]
    public async Task NoKey_KeyedCall_WithoutSensitiveParameters_UsesPlainSha256_AndRuns()
    {
        var transportCalls = 0;
        var probe = new Probe(CkMethodInvocationFilterOrder.Idempotency);
        var response = await Run(Fingerprinter(Keys()), Keyed("{\"hint\":\"x\"}"), TestData.DefinitionWithoutSensitive(),
            () => transportCalls++, probe);

        Assert.True(response.Success);
        Assert.Equal(1, transportCalls);
        Assert.Equal(CkMethodParametersFingerprinter.UnkeyedKeyId, probe.Facts!.ParametersFingerprint!.KeyId);
    }

    [Fact]
    public async Task TheRefusal_IsSeenByAnOuterAuditFilter_AndAnAuthorizationRefusalComesFirst()
    {
        var audit = new Probe(CkMethodInvocationFilterOrder.Audit);
        var response = await Run(Fingerprinter(Keys()), Keyed(), TestData.Definition(), () => { }, audit);
        var auth = new Refuser(CkMethodInvocationFilterOrder.Authorization);
        var refused = await Run(Fingerprinter(Keys()), Keyed(), TestData.Definition(), () => { }, auth);

        Assert.Equal(1, audit.Calls);
        Assert.Equal(CkMethodErrorCodes.MethodValidationFailed, response.Error!.Code);
        Assert.Equal(CkMethodErrorCodes.MethodForbidden, refused.Error!.Code);
    }

    // ---------------------------------------------------------------- unkeyed calls cost nothing

    [Fact]
    public async Task UnkeyedCall_ComputesNoFingerprint_AndIsNeverRefused()
    {
        var counting = new CountingFingerprinter();
        var probe = new Probe(CkMethodInvocationFilterOrder.Idempotency);
        var request = Keyed() with { IdempotencyKey = null };
        var response = await Run(counting, request, TestData.Definition(), () => { }, probe);

        Assert.True(response.Success);
        Assert.Equal(0, counting.Calls);
        Assert.Null(probe.Facts!.ParametersFingerprint);
        Assert.Empty(probe.Facts.AlternateParametersFingerprints);
    }

    [Fact]
    public async Task WithoutTheFingerprinterRegistered_AKeyedCallIsNotFingerprintedNorRefused()
    {
        var probe = new Probe(CkMethodInvocationFilterOrder.Idempotency);
        var response = await Run(null, Keyed(), TestData.Definition(), () => { }, probe);

        Assert.True(response.Success);
        Assert.Null(probe.Facts!.ParametersFingerprint);
    }

    // ---------------------------------------------------------------- before redaction

    [Fact]
    public async Task FingerprintComesFromRawValues_WhileFiltersSeeOnlyTheRedactedView()
    {
        var f = Fingerprinter(Keys(KeyOne));
        var probe = new Probe(CkMethodInvocationFilterOrder.Idempotency);
        var request = Keyed();
        await Run(f, request, TestData.Definition(), () => { }, probe);

        Assert.DoesNotContain(TestData.Secret, probe.SeenParameters);
        Assert.Contains("***", probe.SeenParameters);
        Assert.True(probe.Facts!.MatchesStoredFingerprint(Primary(f, request)));

        // the fingerprint differs for another raw password although the redacted view is identical
        var other = request with { ParametersJson = request.ParametersJson!.Replace(TestData.Secret, "another-Pa55") };
        var probe2 = new Probe(CkMethodInvocationFilterOrder.Idempotency);
        await Run(f, other, TestData.Definition(), () => { }, probe2);
        Assert.Equal(probe.SeenParameters, probe2.SeenParameters);
        Assert.False(probe2.Facts!.ParametersFingerprint!.Matches(probe.Facts.ParametersFingerprint));
    }

    // ---------------------------------------------------------------- canary

    [Fact]
    public async Task Canary_FingerprintKeyMaterialAndRawValuesAppearInNoLogExceptionToStringOrAuditOutput()
    {
        const string rawValue = "RAW-SENTINEL-41d8cd98-Pw";
        var options = Keys(KeyOne, KeyTwo);
        var f = Fingerprinter(options);
        var request = Keyed($"{{\"currentPassword\":\"{rawValue}\",\"newPassword\":\"{rawValue}-2\"}}");
        var audit = new Probe(CkMethodInvocationFilterOrder.Audit);
        var logs = new FakeLogCollector();

        var ok = await Run(f, request, TestData.Definition(), () => { }, logs, audit);
        var facts = audit.Facts!;
        var refused = await Run(Fingerprinter(Keys()), request, TestData.Definition(), () => { }, logs, audit);

        var fingerprint = facts.ParametersFingerprint!;
        var hex = Convert.ToHexString(fingerprint.ToArray());
        var secrets = new[]
        {
            rawValue, KeyOne, KeyTwo, hex, hex.ToLowerInvariant(), Convert.ToBase64String(fingerprint.ToArray())
        };
        var text = string.Join("\n",
            logs.GetSnapshot().Select(r => r.Message + r.Exception),
            audit.Records,
            ok, refused, JsonSerializer.Serialize(refused), JsonSerializer.Serialize(ok),
            fingerprint, facts, options, options.Current, options.Previous, f.Compute(request, true).Primary,
            new InvalidOperationException(fingerprint.ToString()));
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, text);
        }

        Assert.Contains("[redacted]", fingerprint.ToString());
        Assert.Contains("[redacted]", options.Current.ToString());
    }

    [Fact]
    public void Canary_TheValidatorNamesTheSettingNeverTheValue()
    {
        const string bad = "not-base64-SENTINEL-secret!";
        var result = new CkMethodFingerprintOptionsValidator().Validate(null,
            new CkMethodFingerprintOptions { Current = new() { Id = "k1", Secret = bad } });

        Assert.True(result.Failed);
        Assert.DoesNotContain(bad, string.Join("\n", result.Failures!));
        Assert.Contains("Methods:Idempotency:Fingerprint:Current:Secret", string.Join("\n", result.Failures!));
    }

    // ---------------------------------------------------------------- options

    [Fact]
    public void Options_AreValidated_AtTheStartOfTheService()
    {
        var validator = new CkMethodFingerprintOptionsValidator();
        string[] shortSecret = [Convert.ToBase64String(new byte[16])];

        Assert.True(validator.Validate(null, Keys()).Succeeded); // no key at all is fine (fail closed per call)
        Assert.True(validator.Validate(null, Keys(KeyOne)).Succeeded);
        Assert.True(validator.Validate(null, Keys(KeyOne, KeyTwo)).Succeeded);
        Assert.True(validator.Validate(null, Keys(shortSecret[0])).Failed);
        Assert.True(validator.Validate(null, Keys(KeyOne, currentId: "bad id!")).Failed);
        Assert.True(validator.Validate(null, new CkMethodFingerprintOptions { Current = new() { Id = "k1" } }).Failed);
        Assert.True(validator.Validate(null, new CkMethodFingerprintOptions { Current = new() { Secret = KeyOne } }).Failed);
        Assert.True(validator.Validate(null, Keys(null, KeyTwo)).Failed); // previous without current
        Assert.True(validator.Validate(null, Keys(KeyOne, KeyTwo, "same", "same")).Failed);
    }

    [Fact]
    public void Configuration_BindsTheKeysFromMethodsIdempotencyFingerprint_AndRegistersTheFingerprinter()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Methods:Idempotency:Fingerprint:Current:Id"] = "k-2026-10",
            ["Methods:Idempotency:Fingerprint:Current:Secret"] = KeyOne,
            ["Methods:Idempotency:Fingerprint:Previous:Id"] = "k-2026-04",
            ["Methods:Idempotency:Fingerprint:Previous:Secret"] = KeyTwo
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddCkMethodIdempotencyOptions();
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<CkMethodFingerprintOptions>>().Value;
        var result = provider.GetRequiredService<ICkMethodParametersFingerprinter>().Compute(Keyed(), true);

        Assert.Equal("k-2026-10", options.Current.Id);
        Assert.Equal("k-2026-04", options.Previous.Id);
        Assert.Equal("k-2026-10", result.Primary!.KeyId);
        Assert.Equal("k-2026-04", Assert.Single(result.Alternates).KeyId);
    }

    [Fact]
    public void Configuration_AMalformedKey_FailsTheStart()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Methods:Idempotency:Fingerprint:Current:Id"] = "k1",
            ["Methods:Idempotency:Fingerprint:Current:Secret"] = "too-short"
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddCkMethodIdempotencyOptions();
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<CkMethodFingerprintOptions>>().Value);
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<CkMethodInvocationFacts> FactsOf(ICkMethodParametersFingerprinter f, CkMethodRequest request,
        CkMethodDto? definition = null)
    {
        var probe = new Probe(CkMethodInvocationFilterOrder.Idempotency);
        await Run(f, request, definition ?? TestData.Definition(), () => { }, probe);
        return probe.Facts!;
    }

    private static Task<CkMethodResponse> Run(ICkMethodParametersFingerprinter? f, CkMethodRequest request,
        CkMethodDto? definition, Action onTransport, params ICkMethodInvocationFilter[] filters) =>
        Run(f, request, definition, onTransport, new FakeLogCollector(), filters);

    private static Task<CkMethodResponse> Run(ICkMethodParametersFingerprinter? f, CkMethodRequest request,
        CkMethodDto? definition, Action onTransport, FakeLogCollector logs, params ICkMethodInvocationFilter[] filters)
    {
        var loggers = LoggerFactory.Create(b => b.AddProvider(new FakeLoggerProvider(logs)));
        var pipeline = new CkMethodInvocationPipeline(filters, new DefinitionSensitivity(definition),
            loggers.CreateLogger<CkMethodInvocationPipeline>(), f);
        return pipeline.ExecuteAsync(CkMethodTransportNames.Bus, request, r =>
        {
            onTransport();
            return Task.FromResult(CkMethodResponse.Succeeded(r.InvocationId, "h1", "\"ok\""));
        }, TestContext.Current.CancellationToken);
    }

    private sealed class Probe(int order) : ICkMethodInvocationFilter
    {
        public int Calls { get; private set; }
        public CkMethodInvocationFacts? Facts { get; private set; }
        public string SeenParameters { get; private set; } = string.Empty;
        public List<string> Records { get; } = [];
        public int Order => order;

        public async Task<CkMethodResponse> InvokeAsync(CkMethodInvocationContext context, CkMethodInvocationDelegate next)
        {
            Calls++;
            Facts = context.Facts;
            SeenParameters = context.Request.ParametersJson ?? string.Empty;
            var response = await next(context.Request);
            Records.Add(context.Request + "|" + context.Request.ParametersJson + "|" + context.Facts + "|" + response);
            return response;
        }
    }

    private sealed class Refuser(int order) : ICkMethodInvocationFilter
    {
        public int Order => order;

        public Task<CkMethodResponse> InvokeAsync(CkMethodInvocationContext context, CkMethodInvocationDelegate next) =>
            Task.FromResult(CkMethodResponse.Failed(context.Request.InvocationId, string.Empty,
                CkMethodError.Create(CkMethodErrorCodes.MethodForbidden)));
    }

    private sealed class CountingFingerprinter : ICkMethodParametersFingerprinter
    {
        public int Calls { get; private set; }

        public CkMethodFingerprintResult Compute(CkMethodRequest rawRequest, bool hasSensitiveParameters)
        {
            Calls++;
            return new CkMethodFingerprintResult(null, [], false);
        }
    }
}
