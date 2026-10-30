using Meshmakers.Octo.Services.Methods.Handlers;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.Tests.Handlers;

public class CkMethodSeenInvocationsTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));

    private CkMethodSeenInvocations Create() => new(_time);

    private DateTime InSeconds(int s) => _time.GetUtcNow().UtcDateTime.AddSeconds(s);

    [Fact]
    public void NewId_IsFresh_AndKnownId_IsAlreadySeen()
    {
        var seen = Create();
        var request = TestData.Request(InSeconds(30));

        Assert.Equal(CkMethodDeliveryDecision.Fresh, seen.Decide(request));
        Assert.Equal(CkMethodDeliveryDecision.AlreadySeen, seen.Decide(request));
    }

    [Fact]
    public void FlaggedDelivery_IsPossibleDuplicate_EvenForANewId()
    {
        var seen = Create();

        Assert.Equal(CkMethodDeliveryDecision.PossibleDuplicate,
            seen.Decide(TestData.Request(InSeconds(30)), TestDelivery.Redelivered));
    }

    [Fact]
    public void RetriedDelivery_IsPossibleDuplicate()
    {
        var seen = Create();

        Assert.Equal(CkMethodDeliveryDecision.PossibleDuplicate,
            seen.Decide(TestData.Request(InSeconds(30)), TestDelivery.Retried));
    }

    [Fact]
    public void UnflaggedDelivery_IsFresh()
    {
        Assert.Equal(CkMethodDeliveryDecision.Fresh,
            Create().Decide(TestData.Request(InSeconds(30)), TestDelivery.First));
    }

    [Fact]
    public void RefusedFlaggedDelivery_IsRemembered()
    {
        var seen = Create();
        var request = TestData.Request(InSeconds(30));

        Assert.Equal(CkMethodDeliveryDecision.PossibleDuplicate, seen.Decide(request, TestDelivery.Redelivered));
        Assert.Equal(CkMethodDeliveryDecision.AlreadySeen, seen.Decide(request, TestDelivery.First));
        Assert.Equal(1, seen.Count);
    }

    [Fact]
    public void FlaggedDelivery_OfAKnownId_StaysPossibleDuplicate()
    {
        var seen = Create();
        var request = TestData.Request(InSeconds(30));
        seen.Decide(request);

        Assert.Equal(CkMethodDeliveryDecision.PossibleDuplicate, seen.Decide(request, TestDelivery.Redelivered));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExtremeDeadline_DoesNotThrow(bool maxValue)
    {
        var seen = Create();
        var deadline = DateTime.SpecifyKind(maxValue ? DateTime.MaxValue : DateTime.MinValue, DateTimeKind.Utc);
        var request = TestData.Request(deadline);

        Assert.Equal(CkMethodDeliveryDecision.Fresh, seen.Decide(request));
        Assert.Equal(CkMethodDeliveryDecision.AlreadySeen, seen.Decide(request));
    }

    [Fact]
    public void EndlessDeadline_IsKeptAtMostOneHour()
    {
        var seen = Create();
        var endless = TestData.Request(DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc));
        seen.Decide(endless);

        // just inside the hour the id is still known
        _time.Advance(TimeSpan.FromMinutes(59));
        Assert.Equal(CkMethodDeliveryDecision.AlreadySeen, seen.Decide(endless));

        // after 61 minutes the id is evicted by the next cleanup (every 256 adds)
        _time.Advance(TimeSpan.FromMinutes(2));
        for (var i = 0; i < 256; i++)
        {
            seen.Decide(TestData.Request(InSeconds(1)));
        }

        Assert.True(seen.Count <= 256);
        Assert.Equal(CkMethodDeliveryDecision.Fresh, seen.Decide(endless));
    }

    [Fact]
    public void RegularDeadline_IsKeptUntilDeadlinePlusFiveMinutes()
    {
        var seen = Create();
        var request = TestData.Request(InSeconds(60));
        seen.Decide(request);

        _time.Advance(TimeSpan.FromMinutes(5));
        for (var i = 0; i < 256; i++)
        {
            seen.Decide(TestData.Request(InSeconds(10)));
        }

        // 5 min after now is before deadline (60 s) + 5 min: still remembered
        Assert.Equal(CkMethodDeliveryDecision.AlreadySeen, seen.Decide(request));

        _time.Advance(TimeSpan.FromMinutes(1.5));
        for (var i = 0; i < 256; i++)
        {
            seen.Decide(TestData.Request(InSeconds(10)));
        }

        Assert.Equal(CkMethodDeliveryDecision.Fresh, seen.Decide(request));
    }

    [Fact]
    public void NullRequest_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => Create().Decide(null!));
    }
}
