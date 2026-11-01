using System.Text.Json;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods.Handlers;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.Tests.Handlers;

/// <summary>
///     AB#8020, rule R3: a handler write the database did not acknowledge in time may still be on the primary, so the caller
///     gets <c>METHOD_OUTCOME_UNKNOWN</c>; every other exception stays <c>METHOD_HANDLER_FAILED</c>.
/// </summary>
public class CkMethodWriteOutcomeTests
{
    public static TheoryData<string> UnknownFaults => new()
    {
        nameof(MongoWriteFaults.WriteConcernTimeoutOnCommand),
        nameof(MongoWriteFaults.WriteConcernTimeoutOnSingleWrite),
        nameof(MongoWriteFaults.WriteConcernTimeoutOnBulkWrite),
        nameof(MongoWriteFaults.CommitNetworkErrorWithUnknownResult)
    };

    private static Exception Fault(string name) => name switch
    {
        nameof(MongoWriteFaults.WriteConcernTimeoutOnCommand) => MongoWriteFaults.WriteConcernTimeoutOnCommand(),
        nameof(MongoWriteFaults.WriteConcernTimeoutOnSingleWrite) => MongoWriteFaults.WriteConcernTimeoutOnSingleWrite(),
        nameof(MongoWriteFaults.WriteConcernTimeoutOnBulkWrite) => MongoWriteFaults.WriteConcernTimeoutOnBulkWrite(),
        nameof(MongoWriteFaults.CommitNetworkErrorWithUnknownResult) => MongoWriteFaults.CommitNetworkErrorWithUnknownResult(),
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };

    [Theory]
    [MemberData(nameof(UnknownFaults))]
    public void UnacknowledgedWrite_IsUnknown(string fault) =>
        Assert.True(CkMethodWriteOutcome.IsUnknown(Fault(fault)));

    [Theory]
    [MemberData(nameof(UnknownFaults))]
    public void UnacknowledgedWrite_WrappedByThePersistenceLayer_IsUnknown(string fault)
    {
        // the repositories wrap driver exceptions (OperationFailedException.DatabaseOperationFailed keeps the inner one)
        var wrapped = new InvalidOperationException("Database operation 'ReplaceByIdAsync' failed.",
            new InvalidOperationException("inner wrapper", Fault(fault)));

        Assert.True(CkMethodWriteOutcome.IsUnknown(wrapped));
        Assert.True(CkMethodWriteOutcome.IsUnknown(new AggregateException(new TimeoutException(), wrapped)));
    }

    [Fact]
    public void RefusedWrite_OrdinaryException_OrNothing_IsNotUnknown()
    {
        Assert.False(CkMethodWriteOutcome.IsUnknown(MongoWriteFaults.DuplicateKey()));
        Assert.False(CkMethodWriteOutcome.IsUnknown(MongoWriteFaults.BadValue()));
        Assert.False(CkMethodWriteOutcome.IsUnknown(new InvalidOperationException("db is down")));
        Assert.False(CkMethodWriteOutcome.IsUnknown(new AggregateException()));
        Assert.False(CkMethodWriteOutcome.IsUnknown(null));
    }

    // ---------------------------------------------------------------- the host answer

    [Theory]
    [MemberData(nameof(UnknownFaults))]
    public async Task HandlerWhoseWriteIsNotAcknowledged_AnswersOutcomeUnknown_WithCorrelationId(string fault)
    {
        var correlation = Guid.NewGuid();
        var handler = new ChangePasswordHandler((_, _) => throw Fault(fault));
        var host = HostFixture.With(handler, TestData.Definition());
        var request = TestData.Request(host.InSeconds(15)) with { CorrelationId = correlation };

        var response = await host.Invoker.InvokeAsync(request, TestContext.Current.CancellationToken);

        Assert.False(response.Success);
        Assert.Equal(CkMethodErrorCodes.MethodOutcomeUnknown, response.Error!.Code);
        Assert.Equal(TestData.MethodId, response.Error.Parameters[CkMethodErrorParameters.MethodId]);
        Assert.Equal(correlation.ToString(), response.Error.Parameters[CkMethodErrorParameters.CorrelationId]);
        Assert.Null(response.Error.Details);
        // nothing of the exception and nothing of the parameters reaches the wire
        var wire = JsonSerializer.Serialize(response);
        Assert.DoesNotContain("replication", wire, StringComparison.Ordinal);
        Assert.DoesNotContain("commitTransaction", wire, StringComparison.Ordinal);
        Assert.DoesNotContain(TestData.Secret, wire, StringComparison.Ordinal);
        var record = Assert.Single(host.Logs.GetSnapshot(), r => r.Level == LogLevel.Error);
        Assert.Contains("outcome unknown", record.Message, StringComparison.Ordinal);
        Assert.Contains(correlation.ToString(), record.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(TestData.Secret, host.AllLogText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandlerWhoseWriteIsNotAcknowledged_BehindAPersistenceWrapper_AnswersOutcomeUnknown()
    {
        var handler = new ChangePasswordHandler((_, _) => throw new InvalidOperationException(
            "Database operation 'UpdateOneAsync' failed.", MongoWriteFaults.WriteConcernTimeoutOnSingleWrite()));
        var host = HostFixture.With(handler, TestData.Definition());

        var response = await host.Invoker.InvokeAsync(TestData.Request(host.InSeconds(15)),
            TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodOutcomeUnknown, response.Error!.Code);
    }

    [Fact]
    public async Task HandlerWhoseWriteIsRefused_StaysHandlerFailed()
    {
        var host = HostFixture.With(new ChangePasswordHandler((_, _) => throw MongoWriteFaults.DuplicateKey()),
            TestData.Definition());

        var response = await host.Invoker.InvokeAsync(TestData.Request(host.InSeconds(15)),
            TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodHandlerFailed, response.Error!.Code);
    }

    [Fact]
    public async Task OrdinaryHandlerException_StaysHandlerFailed()
    {
        var host = HostFixture.With(new ChangePasswordHandler((_, _) => throw new InvalidOperationException("db is down")),
            TestData.Definition());

        var response = await host.Invoker.InvokeAsync(TestData.Request(host.InSeconds(15)),
            TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodHandlerFailed, response.Error!.Code);
    }

    [Fact]
    public void Catalog_DescribesTheUnacknowledgedWrite_AndItsCorrelationId()
    {
        var info = Assert.Single(CkMethodErrorCodes.Catalog, c => c.Code == CkMethodErrorCodes.MethodOutcomeUnknown);
        Assert.Equal(CkMethodExecutionState.Unknown, info.Execution);
        Assert.Contains(CkMethodErrorParameters.CorrelationId, info.ParameterNames);
    }
}
