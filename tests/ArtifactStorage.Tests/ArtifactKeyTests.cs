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
