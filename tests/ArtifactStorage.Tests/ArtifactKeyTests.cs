using Meshmakers.Octo.Services.ArtifactStorage;

namespace ArtifactStorage.Tests;

public sealed class ArtifactKeyTests
{
    [Theory]
    [InlineData("a")]
    [InlineData("main/presweep/tenant_1/tenant_1-20261006T120000Z-0f.presweep.octoenc")]
    [InlineData("A-Z_0.9/x")]
    public void IsValid_AcceptsValidKeys(string key)
    {
        Assert.True(ArtifactKey.IsValid(key));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/a")]
    [InlineData("a/")]
    [InlineData("a//b")]
    [InlineData("..")]
    [InlineData("a/.b")]
    [InlineData("a:b")]
    [InlineData("a%2Fb")]
    public void IsValid_RejectsInvalidKeys(string? key)
    {
        Assert.False(ArtifactKey.IsValid(key));
    }

    [Fact]
    public void IsValid_EnforcesLengthLimits()
    {
        Assert.False(ArtifactKey.IsValid(new string('a', ArtifactKey.MaxSegmentLength + 1)));
        var longKey = string.Join('/', Enumerable.Repeat(new string('a', 200), 6));
        Assert.True(longKey.Length > ArtifactKey.MaxKeyLength);
        Assert.False(ArtifactKey.IsValid(longKey));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a/")]
    [InlineData("a/b")]
    [InlineData("a/b/")]
    public void IsValidPrefix_AcceptsPrefixes(string prefix)
    {
        Assert.True(ArtifactKey.IsValidPrefix(prefix));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("/")]
    [InlineData("a//")]
    [InlineData("../")]
    public void IsValidPrefix_RejectsInvalidPrefixes(string? prefix)
    {
        Assert.False(ArtifactKey.IsValidPrefix(prefix));
    }
}

public sealed class ArtifactKeyBuilderTests
{
    [Fact]
    public void Build_LowerCasesTenantAndFollowsLayout()
    {
        var builder = new ArtifactKeyBuilder("test-2/main");

        Assert.Equal("test-2/main/presweep/mytenant/f.octoenc",
            builder.Build(ArtifactCategories.Presweep, "MyTenant", "f.octoenc"));
        Assert.Equal("test-2/main/tenant-dumps/", builder.CategoryPrefix(ArtifactCategories.TenantDumps));
        Assert.Equal("test-2/main/restore-staging/abc_1/",
            builder.TenantPrefix(ArtifactCategories.RestoreStaging, "ABC_1"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a b")]
    [InlineData("tenant.x")]
    [InlineData("../x")]
    public void Build_RejectsInvalidTenantIds(string tenantId)
    {
        var builder = new ArtifactKeyBuilder("main");

        Assert.Throws<ArgumentException>(() => builder.Build(ArtifactCategories.Presweep, tenantId, "f"));
    }

    [Fact]
    public void Build_RejectsTooLongTenantId()
    {
        var builder = new ArtifactKeyBuilder("main");

        Assert.Throws<ArgumentException>(() => builder.Build(ArtifactCategories.Presweep,
            new string('a', ArtifactKeyBuilder.MaxTenantIdLength + 1), "f"));
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData(".hidden")]
    [InlineData("")]
    public void Build_RejectsInvalidFileNames(string fileName)
    {
        var builder = new ArtifactKeyBuilder("main");

        Assert.Throws<ArgumentException>(() => builder.Build(ArtifactCategories.Presweep, "t", fileName));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/main")]
    [InlineData("main/")]
    public void Constructor_RejectsInvalidInstancePrefix(string prefix)
    {
        Assert.Throws<ArgumentException>(() => new ArtifactKeyBuilder(prefix));
    }

    [Theory]
    [InlineData("other/presweep/t/f")]
    [InlineData("main/presweep/t")]
    [InlineData("main/presweep/t/f/g")]
    [InlineData("main/presweep/UPPER/f")]
    [InlineData("main/presweep/t.x/f")]
    public void TryParse_RejectsForeignKeys(string key)
    {
        Assert.False(new ArtifactKeyBuilder("main").TryParse(key, out var parts));
        Assert.Null(parts);
    }

    [Fact]
    public void BuildNested_FollowsTheLayoutAndLowerCasesTheTenant()
    {
        var builder = new ArtifactKeyBuilder("test-2/main");

        var key = builder.BuildNested(ArtifactCategories.CopyStaging, "Sbeg", "r1",
            "0a1b2c3d/data/slice-0001/part-0.json.gz");

        Assert.Equal("test-2/main/copy-staging/sbeg/r1/0a1b2c3d/data/slice-0001/part-0.json.gz", key);
        Assert.Equal("copy-staging", ArtifactCategories.CopyStaging);
        Assert.Equal("test-2/main/copy-staging/sbeg/r1/", builder.RunPrefix(ArtifactCategories.CopyStaging, "SBEG", "r1"));
        Assert.StartsWith(builder.RunPrefix(ArtifactCategories.CopyStaging, "sbeg", "r1"), key);
    }

    [Fact]
    public void TryParseNested_ReturnsCategoryTenantRunAndSegments()
    {
        var builder = new ArtifactKeyBuilder("main");
        var key = builder.BuildNested(ArtifactCategories.CopyStaging, "sbeg", "r1", "rt1/data/slice-0001/part-0.json.gz");

        Assert.True(builder.TryParseNested(key, out var parts));

        Assert.NotNull(parts);
        Assert.Equal("copy-staging", parts.Category);
        Assert.Equal("sbeg", parts.TenantId);
        Assert.Equal("r1", parts.RunId);
        Assert.Equal(["rt1", "data", "slice-0001", "part-0.json.gz"], parts.Segments);
        Assert.Equal("rt1/data/slice-0001/part-0.json.gz", parts.RelativePath);
    }

    [Fact]
    public void BuildNested_SingleSegmentBelowTheRunFolder_IsValid()
    {
        var builder = new ArtifactKeyBuilder("main");

        var key = builder.BuildNested(ArtifactCategories.CopyStaging, "t", "r", "part-0.json.gz");

        Assert.True(builder.TryParseNested(key, out var parts));
        Assert.Equal(["part-0.json.gz"], parts!.Segments);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a//b")]
    [InlineData("a/")]
    [InlineData("/a")]
    [InlineData("a/../b")]
    [InlineData("..")]
    [InlineData("a/./b")]
    [InlineData("a\\b")]
    [InlineData("a/.hidden")]
    [InlineData("a/b c")]
    [InlineData("a/ü")]
    public void BuildNested_RejectsInvalidRelativePaths(string relativePath)
    {
        var builder = new ArtifactKeyBuilder("main");

        Assert.Throws<ArgumentException>(() =>
            builder.BuildNested(ArtifactCategories.CopyStaging, "t", "r", relativePath));
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData(".hidden")]
    public void BuildNested_RejectsInvalidRunIds(string runId)
    {
        var builder = new ArtifactKeyBuilder("main");

        Assert.Throws<ArgumentException>(() => builder.BuildNested(ArtifactCategories.CopyStaging, "t", runId, "f"));
        Assert.Throws<ArgumentException>(() => builder.RunPrefix(ArtifactCategories.CopyStaging, "t", runId));
    }

    [Fact]
    public void BuildNested_RejectsInvalidTenantAndCategory()
    {
        var builder = new ArtifactKeyBuilder("main");

        Assert.Throws<ArgumentException>(() => builder.BuildNested(ArtifactCategories.CopyStaging,
            new string('a', ArtifactKeyBuilder.MaxTenantIdLength + 1), "r", "f"));
        Assert.Throws<ArgumentException>(() => builder.BuildNested(ArtifactCategories.CopyStaging, "t.x", "r", "f"));
        Assert.Throws<ArgumentException>(() => builder.BuildNested("a/b", "t", "r", "f"));
        Assert.Throws<ArgumentException>(() =>
            builder.BuildNested(ArtifactCategories.CopyStaging, "t", "r", new string('a', ArtifactKey.MaxSegmentLength + 1)));
    }

    [Fact]
    public void BuildNested_RejectsAKeyOverTheLengthLimit()
    {
        var builder = new ArtifactKeyBuilder("main");
        var path = string.Join('/', Enumerable.Repeat(new string('a', 200), 6));

        Assert.Throws<ArgumentException>(() => builder.BuildNested(ArtifactCategories.CopyStaging, "t", "r", path));
    }

    [Fact]
    public void TryParse_StaysStrict_NestedKeyIsNotAThreeSegmentKey()
    {
        var builder = new ArtifactKeyBuilder("main");
        var nested = builder.BuildNested(ArtifactCategories.CopyStaging, "sbeg", "r1", "rt1/data/part-0.json.gz");

        Assert.False(builder.TryParse(nested, out var parts));
        Assert.Null(parts);
    }

    [Fact]
    public void TryParse_ExistingThreeSegmentKeysParseAsBefore()
    {
        var builder = new ArtifactKeyBuilder("main");

        Assert.True(builder.TryParse("main/tenant-dumps/sbeg/dump.octobak.zip", out var parts));
        Assert.Equal(new ArtifactKeyParts("tenant-dumps", "sbeg", "dump.octobak.zip"), parts);
    }

    [Theory]
    [InlineData("main/presweep/t/f")]
    [InlineData("main/copy-staging/t/r")]
    [InlineData("other/copy-staging/t/r/f")]
    [InlineData("main/copy-staging/UPPER/r/f")]
    [InlineData("main/copy-staging/t.x/r/f")]
    [InlineData("main/copy-staging/t/r/")]
    public void TryParseNested_RejectsKeysOfAnotherLayout(string key)
    {
        Assert.False(new ArtifactKeyBuilder("main").TryParseNested(key, out var parts));
        Assert.Null(parts);
    }
}

public sealed class ArtifactMetadataTests
{
    [Theory]
    [InlineData("Kid")]
    [InlineData("1kid")]
    [InlineData("run-id")]
    [InlineData("")]
    public void RejectsPortabilityBreakingPropertyNames(string name)
    {
        Assert.Throws<ArgumentException>(() =>
            new ArtifactMetadata(null, new Dictionary<string, string> { [name] = "v" }));
    }

    [Theory]
    [InlineData("ü")]
    [InlineData("line\nbreak")]
    public void RejectsNonAsciiValues(string value)
    {
        Assert.Throws<ArgumentException>(() =>
            new ArtifactMetadata(null, new Dictionary<string, string> { ["k"] = value }));
    }

    [Fact]
    public void RejectsOversizedProperties()
    {
        Assert.Throws<ArgumentException>(() => new ArtifactMetadata(null,
            new Dictionary<string, string> { ["k"] = new string('v', ArtifactMetadata.MaxTotalPropertySize) }));
    }

    [Fact]
    public void CopiesProperties()
    {
        var source = new Dictionary<string, string> { ["k"] = "v" };
        var metadata = new ArtifactMetadata("text/plain", source);
        source["k"] = "changed";

        Assert.Equal("v", metadata.Properties["k"]);
        Assert.Equal("text/plain", metadata.EffectiveContentType);
        Assert.Equal(ArtifactMetadata.DefaultContentType, ArtifactMetadata.None.EffectiveContentType);
    }
}
