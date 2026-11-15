using System.Security.Claims;
using Meshmakers.Octo.Services.Infrastructure.TenantGeneration;
using Xunit;

namespace Infrastructure.Tests.TenantGeneration;

/// <summary>
///     AB#8999: the tenant generation check (format, evaluator, modes, cache, metric). The middleware and the method
///     transports are covered in their own tests.
/// </summary>
public class TenantGenerationCheckTests
{
    private const string Tenant = "meshtest";

    // Shared vector with identity #304 (OctoTenantGenerationClaimHandler.FormatGeneration, a27681eb).
    private static readonly DateTime VectorUtc = new(2026, 11, 15, 13, 46, 21, 540, DateTimeKind.Utc);
    private const string VectorValue = "1794750381540";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TenantGenerationCheckRequest Request(string? generation, string? tenant = Tenant,
        string clientId = "studio", string transport = TenantGenerationTransports.Http) =>
        new(tenant, generation is null ? null : TenantGenerationClaim.FromValue(generation), transport, clientId);

    // ------------------------------------------------------------------ format (same as identity #304)

    [Fact]
    public void Format_SharedVector()
    {
        Assert.Equal(VectorValue, TenantGenerationFormat.Format(VectorUtc));
    }

    [Fact]
    public void Format_SubMillisecondTicks_AreTruncated()
    {
        Assert.Equal(VectorValue, TenantGenerationFormat.Format(VectorUtc.AddTicks(9_999)));
    }

    [Fact]
    public void Format_UnspecifiedAndLocal_GiveTheSameValueAsUtc()
    {
        Assert.Equal(VectorValue, TenantGenerationFormat.Format(DateTime.SpecifyKind(VectorUtc, DateTimeKind.Unspecified)));
        Assert.Equal(VectorValue, TenantGenerationFormat.Format(VectorUtc.ToLocalTime()));
    }

    [Fact]
    public void Format_Default_IsNull()
    {
        Assert.Null(TenantGenerationFormat.Format(default));
    }

    [Theory]
    [InlineData("1794750381540", true)]
    [InlineData("0", true)]
    [InlineData("+1794750381540", false)]
    [InlineData("-1794750381540", false)]
    [InlineData("01794750381540", false)]
    [InlineData(" 1794750381540", false)]
    [InlineData("1794750381540 ", false)]
    [InlineData("1794750381540.0", false)]
    [InlineData("1.79475038154E12", false)]
    [InlineData("", false)]
    [InlineData("99999999999999999999", false)]
    [InlineData("١٧٩", false)] // non-ASCII digits
    public void IsCanonical(string value, bool expected)
    {
        Assert.Equal(expected, TenantGenerationFormat.IsCanonical(value));
    }

    // ------------------------------------------------------------------ claim reading

    [Fact]
    public void Claim_NotAString_IsNotWellFormed()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(TenantGenerationClaim.ClaimType, VectorValue, ClaimValueTypes.Integer64)], "Bearer"));

        var claim = TenantGenerationClaim.FromPrincipal(principal);

        Assert.NotNull(claim);
        Assert.False(claim.IsWellFormed);
    }

    [Fact]
    public void Claim_TwoValues_IsNotWellFormed()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(TenantGenerationClaim.ClaimType, VectorValue),
            new Claim(TenantGenerationClaim.ClaimType, VectorValue)
        ], "Bearer"));

        Assert.False(TenantGenerationClaim.FromPrincipal(principal)!.IsWellFormed);
    }

    [Fact]
    public void Claim_Absent_IsNull()
    {
        Assert.Null(TenantGenerationClaim.FromPrincipal(new ClaimsPrincipal(new ClaimsIdentity([], "Bearer"))));
    }

    [Fact]
    public void Claim_PrintedForm_NeverShowsTheValue()
    {
        Assert.DoesNotContain(VectorValue, TenantGenerationClaim.FromValue(VectorValue).ToString());
    }

    // ------------------------------------------------------------------ evaluator

    [Fact]
    public async Task SharedVector_Matches()
    {
        using var f = new GenerationFixture();
        f.Lifecycle.Set(Tenant, VectorUtc);

        Assert.Equal(TenantGenerationOutcome.Match,
            await f.Check.EvaluateAsync(Tenant, TenantGenerationClaim.FromValue(VectorValue), Ct));
    }

    [Fact]
    public async Task SubMillisecondTicksInTheRecord_StillMatch()
    {
        using var f = new GenerationFixture();
        f.Lifecycle.Set(Tenant, VectorUtc.AddTicks(1234));

        Assert.Equal(TenantGenerationOutcome.Match,
            await f.Check.EvaluateAsync(Tenant, TenantGenerationClaim.FromValue(VectorValue), Ct));
    }

    [Theory]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Local)]
    public async Task RecordKinds_GiveTheSameResultAsUtc(DateTimeKind kind)
    {
        using var f = new GenerationFixture();
        var created = kind == DateTimeKind.Local
            ? VectorUtc.ToLocalTime()
            : DateTime.SpecifyKind(VectorUtc, DateTimeKind.Unspecified);
        f.Lifecycle.Set(Tenant, created);

        Assert.Equal(TenantGenerationOutcome.Match,
            await f.Check.EvaluateAsync(Tenant, TenantGenerationClaim.FromValue(VectorValue), Ct));
    }

    [Fact]
    public async Task DiffersByOneMillisecond_IsMismatch()
    {
        using var f = new GenerationFixture();
        f.Lifecycle.Set(Tenant, VectorUtc);

        Assert.Equal(TenantGenerationOutcome.Mismatch,
            await f.Check.EvaluateAsync(Tenant, TenantGenerationClaim.FromValue("1794750381539"), Ct));
    }

    [Theory]
    [InlineData("+1794750381540")]
    [InlineData("01794750381540")]
    [InlineData("1794750381540.0")]
    [InlineData("1.79475038154E12")]
    [InlineData(" 1794750381540")]
    [InlineData("abc")]
    public async Task NonCanonicalClaim_IsMismatch(string value)
    {
        using var f = new GenerationFixture();
        f.Lifecycle.Set(Tenant, VectorUtc);

        Assert.Equal(TenantGenerationOutcome.Mismatch,
            await f.Check.EvaluateAsync(Tenant, TenantGenerationClaim.FromValue(value), Ct));
    }

    [Fact]
    public async Task NonStringClaim_IsMismatch()
    {
        using var f = new GenerationFixture();
        f.Lifecycle.Set(Tenant, VectorUtc);

        Assert.Equal(TenantGenerationOutcome.Mismatch,
            await f.Check.EvaluateAsync(Tenant, TenantGenerationClaim.FromValue(VectorValue, isString: false), Ct));
    }

    [Fact]
    public async Task MissingClaim_ForATenantWithGeneration_IsMissing()
    {
        using var f = new GenerationFixture();
        f.Lifecycle.Set(Tenant, VectorUtc);

        Assert.Equal(TenantGenerationOutcome.Missing, await f.Check.EvaluateAsync(Tenant, null, Ct));
    }

    [Fact]
    public async Task LegacyTenant_WithoutRecordOrCreatedUtc_IsNotChecked()
    {
        using var f = new GenerationFixture();
        f.Lifecycle.Set("legacy-no-created", null);

        Assert.Equal(TenantGenerationOutcome.NotChecked, await f.Check.EvaluateAsync("no-record", null, Ct));
        Assert.Equal(TenantGenerationOutcome.NotChecked, await f.Check.EvaluateAsync("legacy-no-created", null, Ct));
    }

    [Fact]
    public async Task SystemTenant_IsNeverChecked_EvenWithAMismatchingClaimAndARecord()
    {
        using var f = new GenerationFixture(TenantGenerationEnforcementMode.Enforce);
        f.Lifecycle.Set("octosystem", VectorUtc);

        var result = await f.Check.CheckAsync(Request("1", tenant: "octosystem"), Ct);

        Assert.Equal(TenantGenerationOutcome.NotChecked, result.Outcome);
        Assert.Equal(TenantGenerationDecision.Allow, result.Decision);
        Assert.Equal(0, f.Lifecycle.Reads);
    }

    [Fact]
    public async Task NoTokenTenant_IsNotChecked()
    {
        using var f = new GenerationFixture(TenantGenerationEnforcementMode.Enforce);

        var result = await f.Check.CheckAsync(Request(VectorValue, tenant: null), Ct);

        Assert.Equal(TenantGenerationOutcome.NotChecked, result.Outcome);
        Assert.Equal(TenantGenerationDecision.Allow, result.Decision);
    }

    [Fact]
    public async Task HostWithoutLifecycleStore_IsNotChecked()
    {
        using var f = new GenerationFixture(TenantGenerationEnforcementMode.Enforce, withStore: false);

        var result = await f.Check.CheckAsync(Request(null), Ct);

        Assert.Equal(TenantGenerationOutcome.NotChecked, result.Outcome);
        Assert.Equal(TenantGenerationDecision.Allow, result.Decision);
    }

    // ------------------------------------------------------------------ modes

    public static TheoryData<TenantGenerationEnforcementMode, string?, bool, TenantGenerationOutcome,
        TenantGenerationDecision> ModeMatrix => new()
    {
        { TenantGenerationEnforcementMode.LogOnly, VectorValue, false, TenantGenerationOutcome.Match, TenantGenerationDecision.Allow },
        { TenantGenerationEnforcementMode.LogOnly, "1", false, TenantGenerationOutcome.Mismatch, TenantGenerationDecision.Allow },
        { TenantGenerationEnforcementMode.LogOnly, null, false, TenantGenerationOutcome.Missing, TenantGenerationDecision.Allow },
        { TenantGenerationEnforcementMode.LogOnly, VectorValue, true, TenantGenerationOutcome.LookupFailed, TenantGenerationDecision.Allow },
        { TenantGenerationEnforcementMode.Enforce, VectorValue, false, TenantGenerationOutcome.Match, TenantGenerationDecision.Allow },
        { TenantGenerationEnforcementMode.Enforce, "1", false, TenantGenerationOutcome.Mismatch, TenantGenerationDecision.Deny },
        { TenantGenerationEnforcementMode.Enforce, null, false, TenantGenerationOutcome.Missing, TenantGenerationDecision.Deny },
        { TenantGenerationEnforcementMode.Enforce, VectorValue, true, TenantGenerationOutcome.LookupFailed, TenantGenerationDecision.Unavailable }
    };

    [Theory]
    [MemberData(nameof(ModeMatrix))]
    public async Task Mode_DecidesTheOutcome(TenantGenerationEnforcementMode mode, string? generation, bool lookupFails,
        TenantGenerationOutcome expectedOutcome, TenantGenerationDecision expectedDecision)
    {
        using var f = new GenerationFixture(mode);
        using var listener = new GenerationCheckListener(f.Metrics);
        f.Lifecycle.Set(Tenant, VectorUtc);
        if (lookupFails)
        {
            f.Lifecycle.Failure = new TimeoutException("mongo down");
        }

        var result = await f.Check.CheckAsync(Request(generation), Ct);

        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.Equal(expectedDecision, result.Decision);
        var measurement = Assert.Single(listener.Measurements);
        Assert.Equal(TenantGenerationMetrics.OutcomeLabel(expectedOutcome),
            measurement.Tags[TenantGenerationMetrics.OutcomeTag]);
        Assert.Equal(TenantGenerationMetrics.ModeLabel(mode), measurement.Tags[TenantGenerationMetrics.ModeTag]);

        var logged = expectedOutcome is TenantGenerationOutcome.Missing or TenantGenerationOutcome.Mismatch;
        Assert.Equal(logged ? 1 : 0, f.Logger.Warnings.Count);
    }

    [Fact]
    public async Task Disabled_NoLookup_NoMetric_NoLog()
    {
        using var f = new GenerationFixture(TenantGenerationEnforcementMode.Disabled);
        using var listener = new GenerationCheckListener(f.Metrics);
        f.Lifecycle.Set(Tenant, VectorUtc);

        var result = await f.Check.CheckAsync(Request("1"), Ct);

        Assert.Equal(TenantGenerationDecision.Allow, result.Decision);
        Assert.Equal(0, f.Lifecycle.Reads);
        Assert.Empty(listener.Measurements);
        Assert.Empty(f.Logger.Warnings);
    }

    [Fact]
    public void DefaultMode_IsLogOnly_AndTheCacheTtlIs30Seconds()
    {
        var options = new Meshmakers.Octo.Services.Infrastructure.Configuration.TenantAuthorizationOptions();

        Assert.Equal(TenantGenerationEnforcementMode.LogOnly, options.TenantGenerationEnforcement);
        Assert.Equal(TimeSpan.FromSeconds(30), options.TenantGenerationCacheDuration);
    }

    [Fact]
    public async Task Log_IsRateLimitedPerClient_AndNeverNamesTheGeneration()
    {
        using var f = new GenerationFixture();
        f.Lifecycle.Set(Tenant, VectorUtc);

        for (var i = 0; i < 50; i++)
        {
            await f.Check.CheckAsync(Request("1794750381539"), Ct);
        }

        await f.Check.CheckAsync(Request("1794750381539", clientId: "octo-cli"), Ct);
        Assert.Equal(2, f.Logger.Warnings.Count);

        f.Time.Advance(TimeSpan.FromMinutes(1));
        await f.Check.CheckAsync(Request("1794750381539"), Ct);
        Assert.Equal(3, f.Logger.Warnings.Count);

        Assert.All(f.Logger.Warnings, w =>
        {
            Assert.DoesNotContain("1794750381539", w);
            Assert.DoesNotContain(VectorValue, w);
        });
    }

    // ------------------------------------------------------------------ cache

    [Fact]
    public async Task CacheHit_NoLifecycleReadPerRequest_1000Requests_OneRead()
    {
        using var f = new GenerationFixture(TenantGenerationEnforcementMode.Enforce);
        f.Lifecycle.Set(Tenant, VectorUtc);

        for (var i = 0; i < 1000; i++)
        {
            var result = await f.Check.CheckAsync(Request(VectorValue), Ct);
            Assert.Equal(TenantGenerationDecision.Allow, result.Decision);
        }

        Assert.Equal(1, f.Lifecycle.Reads);
    }

    [Fact]
    public async Task NoRecordResult_IsCachedToo()
    {
        using var f = new GenerationFixture(TenantGenerationEnforcementMode.Enforce);

        for (var i = 0; i < 100; i++)
        {
            await f.Check.CheckAsync(Request(null, tenant: "legacy"), Ct);
        }

        Assert.Equal(1, f.Lifecycle.Reads);
    }

    [Fact]
    public async Task AfterTheTtl_TheRecordIsReadAgain()
    {
        using var f = new GenerationFixture();
        f.Lifecycle.Set(Tenant, VectorUtc);

        await f.Check.CheckAsync(Request(VectorValue), Ct);
        f.Time.Advance(TimeSpan.FromSeconds(29));
        await f.Check.CheckAsync(Request(VectorValue), Ct);
        Assert.Equal(1, f.Lifecycle.Reads);

        f.Time.Advance(TimeSpan.FromSeconds(2));
        await f.Check.CheckAsync(Request(VectorValue), Ct);
        Assert.Equal(2, f.Lifecycle.Reads);
    }

    [Fact]
    public async Task ConcurrentMisses_ForOneTenant_CauseOneRead()
    {
        using var f = new GenerationFixture();
        f.Lifecycle.Set(Tenant, VectorUtc);
        f.Lifecycle.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var calls = Enumerable.Range(0, 50).Select(_ => f.Check.CheckAsync(Request(VectorValue), Ct).AsTask()).ToList();
        // let every caller reach the shared read
        await Task.Delay(50, Ct);
        f.Lifecycle.Gate.SetResult();
        var results = await Task.WhenAll(calls);

        Assert.All(results, r => Assert.Equal(TenantGenerationOutcome.Match, r.Outcome));
        Assert.Equal(1, f.Lifecycle.Reads);
    }

    [Fact]
    public async Task FailedLookup_IsRetriedAfterAShortBackOff_NotPerRequest()
    {
        using var f = new GenerationFixture(TenantGenerationEnforcementMode.Enforce);
        f.Lifecycle.Set(Tenant, VectorUtc);
        f.Lifecycle.Failure = new TimeoutException();

        for (var i = 0; i < 100; i++)
        {
            Assert.Equal(TenantGenerationDecision.Unavailable,
                (await f.Check.CheckAsync(Request(VectorValue), Ct)).Decision);
        }

        Assert.Equal(1, f.Lifecycle.Reads);

        f.Lifecycle.Failure = null;
        f.Time.Advance(TenantGenerationReader.FailureBackoff);
        Assert.Equal(TenantGenerationDecision.Allow, (await f.Check.CheckAsync(Request(VectorValue), Ct)).Decision);
        Assert.Equal(2, f.Lifecycle.Reads);
    }

    [Fact]
    public async Task CacheDuration_AboveTheMaximum_IsClamped()
    {
        using var f = new GenerationFixture(cacheDuration: TimeSpan.FromMinutes(10));
        f.Lifecycle.Set(Tenant, VectorUtc);

        await f.Check.CheckAsync(Request(VectorValue), Ct);
        f.Time.Advance(TenantGenerationReader.MaxCacheDuration);
        await f.Check.CheckAsync(Request(VectorValue), Ct);

        Assert.Equal(2, f.Lifecycle.Reads);
    }

    [Fact]
    public async Task CacheDuration_Zero_ReadsEveryTime()
    {
        using var f = new GenerationFixture(cacheDuration: TimeSpan.Zero);
        f.Lifecycle.Set(Tenant, VectorUtc);

        await f.Check.CheckAsync(Request(VectorValue), Ct);
        await f.Check.CheckAsync(Request(VectorValue), Ct);

        Assert.Equal(2, f.Lifecycle.Reads);
    }

    [Fact]
    public async Task RecreatedTenant_NewerTokenGeneration_RefreshesTheCacheOnce()
    {
        using var f = new GenerationFixture(TenantGenerationEnforcementMode.Enforce);
        f.Lifecycle.Set(Tenant, VectorUtc);
        Assert.Equal(TenantGenerationOutcome.Match, (await f.Check.CheckAsync(Request(VectorValue), Ct)).Outcome);

        // delete + recreate, this instance missed the events: the cached value is the old generation
        var recreated = VectorUtc.AddMinutes(3);
        f.Lifecycle.Set(Tenant, recreated);
        f.Time.Advance(TimeSpan.FromSeconds(2));

        var fresh = await f.Check.CheckAsync(Request(TenantGenerationFormat.Format(recreated)), Ct);
        Assert.Equal(TenantGenerationOutcome.Match, fresh.Outcome);
        Assert.Equal(2, f.Lifecycle.Reads);

        // the old token now mismatches without another read (it is older than the cached generation)
        var old = await f.Check.CheckAsync(Request(VectorValue), Ct);
        Assert.Equal(TenantGenerationOutcome.Mismatch, old.Outcome);
        Assert.Equal(TenantGenerationDecision.Deny, old.Decision);
        Assert.Equal(2, f.Lifecycle.Reads);
    }

    [Fact]
    public async Task OlderTokenGeneration_DoesNotRefresh()
    {
        using var f = new GenerationFixture(TenantGenerationEnforcementMode.Enforce);
        f.Lifecycle.Set(Tenant, VectorUtc);
        await f.Check.CheckAsync(Request(VectorValue), Ct);

        for (var i = 0; i < 100; i++)
        {
            Assert.Equal(TenantGenerationDecision.Deny, (await f.Check.CheckAsync(Request("1"), Ct)).Decision);
        }

        Assert.Equal(1, f.Lifecycle.Reads);
    }

    [Fact]
    public async Task Invalidate_DropsTheCachedValue()
    {
        using var f = new GenerationFixture();
        f.Lifecycle.Set(Tenant, VectorUtc);
        await f.Check.CheckAsync(Request(VectorValue), Ct);

        f.Reader.Invalidate("MeshTest");
        await f.Check.CheckAsync(Request(VectorValue), Ct);

        Assert.Equal(2, f.Lifecycle.Reads);
    }

    // ------------------------------------------------------------------ metric

    [Fact]
    public async Task Metric_LabelSet_IsExactlyOutcomeModeTransportClientId()
    {
        using var f = new GenerationFixture(TenantGenerationEnforcementMode.Enforce);
        using var listener = new GenerationCheckListener(f.Metrics);
        f.Lifecycle.Set(Tenant, VectorUtc);

        await f.Check.CheckAsync(Request("1794750381539", clientId: "octo-cli",
            transport: TenantGenerationTransports.Method), Ct);

        var measurement = Assert.Single(listener.Measurements);
        Assert.Equal(1, measurement.Value);
        Assert.Equal(
            new[]
            {
                TenantGenerationMetrics.ClientIdTag, TenantGenerationMetrics.ModeTag, TenantGenerationMetrics.OutcomeTag,
                TenantGenerationMetrics.TransportTag
            },
            measurement.Tags.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("mismatch", measurement.Tags["outcome"]);
        Assert.Equal("enforce", measurement.Tags["mode"]);
        Assert.Equal("method", measurement.Tags["transport"]);
        Assert.Equal("octo-cli", measurement.Tags["client_id"]);

        // never tenant, user, sub, tenant_gen or a token value
        foreach (var value in measurement.Tags.Values)
        {
            Assert.NotEqual(Tenant, value);
            Assert.NotEqual("1794750381539", value);
            Assert.NotEqual(VectorValue, value);
        }
    }

    [Fact]
    public async Task Metric_GrantType_IsOnlyAddedWhenGiven()
    {
        using var f = new GenerationFixture();
        using var listener = new GenerationCheckListener(f.Metrics);
        f.Lifecycle.Set(Tenant, VectorUtc);

        await f.Check.CheckAsync(new TenantGenerationCheckRequest(Tenant, TenantGenerationClaim.FromValue(VectorValue),
            TenantGenerationTransports.Grant, "octo-cli", "refresh_token"), Ct);

        var measurement = Assert.Single(listener.Measurements);
        Assert.Equal("refresh_token", measurement.Tags[TenantGenerationMetrics.GrantTypeTag]);
        Assert.Equal(5, measurement.Tags.Count);
    }

    [Fact]
    public async Task Metric_GrantType_OutsideTheFixedSet_IsOther()
    {
        using var f = new GenerationFixture();
        using var listener = new GenerationCheckListener(f.Metrics);

        await f.Check.CheckAsync(new TenantGenerationCheckRequest(Tenant, null, TenantGenerationTransports.Grant,
            "octo-cli", "urn:custom:anything"), Ct);

        Assert.Equal("other", Assert.Single(listener.Measurements).Tags[TenantGenerationMetrics.GrantTypeTag]);
    }

    [Theory]
    [InlineData(null, "unknown")]
    [InlineData("", "unknown")]
    [InlineData("has space", "unknown")]
    [InlineData("studio", "studio")]
    public async Task Metric_ClientId_IsBounded(string? clientId, string expected)
    {
        using var f = new GenerationFixture();
        using var listener = new GenerationCheckListener(f.Metrics);

        await f.Check.CheckAsync(new TenantGenerationCheckRequest(Tenant, null, TenantGenerationTransports.Http,
            clientId), Ct);

        Assert.Equal(expected, Assert.Single(listener.Measurements).Tags[TenantGenerationMetrics.ClientIdTag]);
    }

    [Fact]
    public void Metric_MeterAndCounterNames_ArePinned()
    {
        // ObservabilityBuilder registers the meter by this string (MeterRegistrationTests)
        Assert.Equal("Meshmakers.Octo.TenantAuthorization", TenantGenerationMetrics.MeterName);
        Assert.Equal("octo.tenant_auth.generation_checks", TenantGenerationMetrics.CounterName);
    }

    [Fact]
    public void Metric_ClientIdBudget_FallsBackToOther()
    {
        using var metrics = new TenantGenerationMetrics();
        for (var i = 0; i < TenantGenerationMetrics.MaxClientIds; i++)
        {
            Assert.Equal($"client-{i}", metrics.ClientIdLabel($"client-{i}"));
        }

        Assert.Equal(TenantGenerationMetrics.OtherClientId, metrics.ClientIdLabel("one-too-many"));
        Assert.Equal("client-1", metrics.ClientIdLabel("client-1"));
    }
}
