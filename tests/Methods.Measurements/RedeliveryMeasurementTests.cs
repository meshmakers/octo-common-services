using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods.Handlers;
using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Services.Methods.Measurements;

/// <summary>
///     How often does the broker flag a redelivery under forced failure, and does a duplicate ever run unflagged (AB#7823,
///     OD-3 option A, trigger of option B')? Two handler replicas share the method queue behind one dispatcher; while calls
///     are in flight one replica is taken away in one of three ways: its broker connection is closed
///     (<c>rabbitmqctl close_connection</c>), the replica process is hard-killed (SIGKILL of a child process this test
///     started), or the replica is stopped gracefully. Per scenario several rounds of calls are made; per invocation id the
///     test counts how often the handler body ran. An id that ran twice is a duplicate the broker did not flag (the flag
///     makes a handler host refuse the delivery without running it); when the two runs were on different instances it is
///     the unrecognized cross-instance duplicate that the metric <c>ck.method.duplicate_refused</c> can never show.
/// </summary>
/// <remarks>
///     A measurement, not a test: skipped unless <c>OCTO_METHOD_MEASURE=1</c>. The numbers go to the test output (lines
///     starting with <c>RESULT</c>); nothing is asserted about their size except the invariants that every call is answered
///     and that no answer is lost. Private containers only (see <see cref="MeasurementFixture" />). Rounds per scenario:
///     <c>OCTO_METHOD_MEASURE_ROUNDS</c> (default 10 rounds of 300 calls = 3000 invocations per scenario).
/// </remarks>
[Collection(MeasurementCollection.Name)]
[Trait("Category", "Measurement")]
public sealed class RedeliveryMeasurementTests(MeasurementFixture fixture, ITestOutputHelper output)
{
    private const int CallsPerRound = 300;
    private const int Concurrency = 32;
    private const int TriggerAfterCompleted = 10;
    private static readonly TimeSpan HandlerWork = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(20);

    public enum Scenario
    {
        ConnectionClose,
        HardKill,
        GracefulStop
    }

    public static TheoryData<Scenario> Scenarios => [Scenario.ConnectionClose, Scenario.HardKill, Scenario.GracefulStop];

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task ForcedFailure_WithTwoReplicas_CountsExecutionsRedeliveriesAndDuplicates(Scenario scenario)
    {
        Assert.SkipUnless(MeasurementFixture.Enabled, MeasurementFixture.SkipReason);
        var rounds = int.TryParse(Environment.GetEnvironmentVariable("OCTO_METHOD_MEASURE_ROUNDS"), out var r) ? r : 10;

        // the broker's own redelivery counter, an independent witness of the library's flag count (stats flush every 5 s)
        var brokerBefore = await BrokerRedeliveredAsync(settle: true);
        using var listener = new RefusalListener();
        var total = new Tally();
        for (var round = 0; round < rounds; round++)
        {
            total.Add(await RunRoundAsync(scenario, listener));
        }

        var brokerAfter = await BrokerRedeliveredAsync(settle: true);
        total.BrokerRedelivered = brokerAfter - brokerBefore;

        output.WriteLine(total.Format(scenario, rounds));
        Assert.Equal(total.Invocations, total.Success + total.OutcomeUnknown + total.Timeout + total.OtherFailure);
        Assert.Equal(rounds * CallsPerRound, total.Invocations);
    }

    // ---- one round --------------------------------------------------------------------------------------------

    private async Task<Tally> RunRoundAsync(Scenario scenario, RefusalListener listener)
    {
        var prefix = $"measure-{Guid.NewGuid():N}";
        var executions = new ConcurrentQueue<(Guid Id, string Instance)>();
        void OnExecute(Guid id, string instance) => executions.Enqueue((id, instance));

        await using var survivor = await MeasurementHost.StartHandlerAsync(fixture.RabbitHost, fixture.RabbitPort, prefix,
            new MeasurementProbe("A", HandlerWork, OnExecute));
        MeasurementHost? victimHost = null;
        ChildReplica? victimProcess = null;
        if (scenario == Scenario.HardKill)
        {
            victimProcess = await ChildReplica.StartAsync(fixture.RabbitHost, fixture.RabbitPort, prefix, "B", HandlerWork, OnExecute);
        }
        else
        {
            victimHost = await MeasurementHost.StartHandlerAsync(fixture.RabbitHost, fixture.RabbitPort, prefix,
                new MeasurementProbe("B", HandlerWork, OnExecute));
        }

        await using var gateway = await StartGatewayAsync(prefix);
        var dispatcher = gateway.Provider.GetRequiredService<ICkMethodDispatcher>();
        var refusalsBefore = listener.Snapshot();

        var results = new ConcurrentBag<(CkMethodRequest Request, CkMethodResponse Response)>();
        var completed = 0;
        Task? injection = null;
        try
        {
            await Parallel.ForEachAsync(Enumerable.Range(0, CallsPerRound),
                new ParallelOptions { MaxDegreeOfParallelism = Concurrency, CancellationToken = TestContext.Current.CancellationToken },
                async (_, ct) =>
                {
                    var request = NewRequest();
                    var response = await dispatcher.InvokeAsync(request, ct);
                    results.Add((request, response));
                    if (Interlocked.Increment(ref completed) == TriggerAfterCompleted)
                    {
                        // not awaited here: the burst keeps running while the failure is forced
                        injection = Task.Run(() => InjectAsync(scenario, victimHost, victimProcess));
                    }
                });
            Assert.NotNull(injection);
            await injection;
        }
        finally
        {
            if (victimProcess != null)
            {
                await victimProcess.DisposeAsync();
            }

            if (victimHost != null)
            {
                await victimHost.DisposeAsync();
            }
        }

        var refusals = listener.Snapshot().Minus(refusalsBefore);
        return Tally.From(results, executions, refusals);
    }

    private async Task InjectAsync(Scenario scenario, MeasurementHost? victimHost, ChildReplica? victimProcess)
    {
        switch (scenario)
        {
            case Scenario.ConnectionClose:
            {
                // the consumer connection that holds the most unacknowledged messages: a lost connection
                var channels = await fixture.RabbitMqCtlAsync("list_channels", "connection", "messages_unacknowledged");
                var connectionPid = channels.Select(l => l.Split('\t'))
                    .Where(p => p.Length >= 2 && int.TryParse(p[1], out _))
                    .OrderByDescending(p => int.Parse(p[1], CultureInfo.InvariantCulture))
                    .First()[0];
                await fixture.RabbitMqCtlAsync("close_connection", connectionPid, "AB7823-forced-failure");
                break;
            }
            case Scenario.HardKill:
                victimProcess!.Kill(); // SIGKILL of the process this test started: no close, no ack
                break;
            case Scenario.GracefulStop:
                await victimHost!.DisposeAsync();
                break;
        }
    }

    // ---- gateway, requests ------------------------------------------------------------------------------------

    private async Task<MeasurementHost> StartGatewayAsync(string prefix)
    {
        var services = MeasurementHost.BaseServices(fixture.RabbitHost, fixture.RabbitPort, prefix);
        services.AddSingleton<ICkMethodDefinitionSource>(new MeasurementDefinitionSource());
        services.AddDistributionEventHub(c =>
        {
            c.UniqueServiceAddress = "Gateway";
            c.AutomaticallyStartBusDuringStartup = false;
            c.AddCkMethodHandlerRegistryFeed();
        });
        services.AddCkMethodDispatcher();
        return await MeasurementHost.StartAsync(services);
    }

    private static CkMethodRequest NewRequest() => new("meshtest")
    {
        InvocationId = Guid.NewGuid(),
        CorrelationId = Guid.NewGuid(),
        MethodId = MeasurementHost.MethodId,
        CkTypeId = "Sample.Accounts/Account",
        TargetRtId = "acc-1",
        ParametersJson = JsonSerializer.Serialize(new { newPassword = "correct-horse-battery" }),
        DeadlineUtc = DateTime.UtcNow + CallTimeout
    };

    // ---- the broker's own redelivery counter ------------------------------------------------------------------

    private async Task<long> BrokerRedeliveredAsync(bool settle)
    {
        if (settle)
        {
            await Task.Delay(TimeSpan.FromSeconds(7), TestContext.Current.CancellationToken); // management stats interval 5 s
        }

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes("guest:guest")));
        var json = await http.GetStringAsync($"http://{fixture.RabbitHost}:{fixture.RabbitManagementPort}/api/overview",
            TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("message_stats", out var stats) && stats.TryGetProperty("redeliver", out var n)
            ? n.GetInt64()
            : 0;
    }

    // ---- counting ---------------------------------------------------------------------------------------------

    /// <summary>Counts <c>ck.method.duplicate_refused</c> by reason, process wide (all in-process hosts).</summary>
    private sealed class RefusalListener : IDisposable
    {
        private readonly MeterListener _listener = new();
        private long _brokerFlag;
        private long _seenId;

        public RefusalListener()
        {
            _listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == CkMethodTelemetry.MeterName && instrument.Name == "ck.method.duplicate_refused")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (tag.Key != "reason")
                    {
                        continue;
                    }

                    if ((string?)tag.Value == "broker_flag")
                    {
                        Interlocked.Add(ref _brokerFlag, value);
                    }
                    else if ((string?)tag.Value == "seen_id")
                    {
                        Interlocked.Add(ref _seenId, value);
                    }
                }
            });
            _listener.Start();
        }

        public Refusals Snapshot() => new(Interlocked.Read(ref _brokerFlag), Interlocked.Read(ref _seenId));

        public void Dispose() => _listener.Dispose();
    }

    private readonly record struct Refusals(long BrokerFlag, long SeenId)
    {
        public Refusals Minus(Refusals other) => new(BrokerFlag - other.BrokerFlag, SeenId - other.SeenId);
    }

    private sealed class Tally
    {
        public int Invocations;
        public int Success;
        public int OutcomeUnknown;
        public int Timeout;
        public int OtherFailure;
        public int Executions;
        public int DistinctExecuted;
        public int RunMoreThanOnce; // unflagged duplicates
        public int RunTwiceOnOneInstance;
        public int RunOnDifferentInstances; // unrecognized cross-instance duplicates
        public int NeverExecuted; // answered, but the handler body never ran for it
        public int ExecutedButNotReportedSuccess; // ran, yet the caller did not get the success
        public long FlaggedRefused;
        public long SeenIdRefused;
        public long BrokerRedelivered;
        public readonly List<string> DuplicateDetails = [];

        public static Tally From(IEnumerable<(CkMethodRequest Request, CkMethodResponse Response)> results,
            IEnumerable<(Guid Id, string Instance)> executions, Refusals refusals)
        {
            var tally = new Tally { FlaggedRefused = refusals.BrokerFlag, SeenIdRefused = refusals.SeenId };
            var byId = executions.GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.Select(e => e.Instance).ToList());
            foreach (var (request, response) in results)
            {
                tally.Invocations++;
                if (response.Success)
                {
                    tally.Success++;
                }
                else if (response.Error!.Code == CkMethodErrorCodes.MethodOutcomeUnknown)
                {
                    tally.OutcomeUnknown++;
                }
                else if (response.Error.Code == CkMethodErrorCodes.MethodTimeout)
                {
                    tally.Timeout++;
                }
                else
                {
                    tally.OtherFailure++;
                }

                byId.TryGetValue(request.InvocationId, out var runs);
                runs ??= [];
                if (runs.Count == 0)
                {
                    tally.NeverExecuted++;
                }
                else if (!response.Success)
                {
                    tally.ExecutedButNotReportedSuccess++;
                }
            }

            foreach (var (id, runs) in byId)
            {
                tally.Executions += runs.Count;
                tally.DistinctExecuted++;
                if (runs.Count > 1)
                {
                    tally.RunMoreThanOnce++;
                    if (runs.Distinct().Count() == 1)
                    {
                        tally.RunTwiceOnOneInstance++;
                    }
                    else
                    {
                        tally.RunOnDifferentInstances++;
                    }

                    tally.DuplicateDetails.Add($"{id} ran {runs.Count}x on [{string.Join(",", runs)}]");
                }
            }

            return tally;
        }

        public void Add(Tally other)
        {
            Invocations += other.Invocations;
            Success += other.Success;
            OutcomeUnknown += other.OutcomeUnknown;
            Timeout += other.Timeout;
            OtherFailure += other.OtherFailure;
            Executions += other.Executions;
            DistinctExecuted += other.DistinctExecuted;
            RunMoreThanOnce += other.RunMoreThanOnce;
            RunTwiceOnOneInstance += other.RunTwiceOnOneInstance;
            RunOnDifferentInstances += other.RunOnDifferentInstances;
            NeverExecuted += other.NeverExecuted;
            ExecutedButNotReportedSuccess += other.ExecutedButNotReportedSuccess;
            FlaggedRefused += other.FlaggedRefused;
            SeenIdRefused += other.SeenIdRefused;
            DuplicateDetails.AddRange(other.DuplicateDetails);
        }

        public string Format(Scenario scenario, int rounds)
        {
            var text = new StringBuilder();
            text.AppendLine($"RESULT {scenario}: rounds={rounds} invocations={Invocations} " +
                            $"executions={Executions} (distinct ids executed={DistinctExecuted}) " +
                            $"flagged_redeliveries_refused={FlaggedRefused} seen_id_refusals={SeenIdRefused} " +
                            $"broker_redeliver_counter={BrokerRedelivered}");
            text.AppendLine($"RESULT {scenario}: unflagged_duplicates={RunMoreThanOnce} " +
                            $"(same_instance={RunTwiceOnOneInstance}, cross_instance_unrecognized={RunOnDifferentInstances})");
            text.AppendLine($"RESULT {scenario}: caller saw success={Success} outcome_unknown={OutcomeUnknown} timeout={Timeout} " +
                            $"other={OtherFailure}; never_executed={NeverExecuted} executed_but_not_success={ExecutedButNotReportedSuccess}");
            foreach (var detail in DuplicateDetails.Take(20))
            {
                text.AppendLine($"RESULT {scenario}: DUPLICATE {detail}");
            }

            return text.ToString();
        }
    }

    // ---- the replica that is hard-killed ----------------------------------------------------------------------

    /// <summary>The worker project as a child process. Killed only through the <see cref="Process" /> this test created.</summary>
    private sealed class ChildReplica : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly Task _reader;

        private ChildReplica(Process process, Task reader)
        {
            _process = process;
            _reader = reader;
        }

        public static async Task<ChildReplica> StartAsync(string host, int port, string prefix, string label, TimeSpan work,
            Action<Guid, string> onExecute)
        {
            var workerDll = typeof(RedeliveryMeasurementTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(a => a.Key == "WorkerDll").Value!;
            Assert.True(File.Exists(workerDll), $"worker not built: {workerDll}");
            var start = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            start.ArgumentList.Add(workerDll);
            start.ArgumentList.Add($"host={host}");
            start.ArgumentList.Add($"port={port}");
            start.ArgumentList.Add($"prefix={prefix}");
            start.ArgumentList.Add($"label={label}");
            start.ArgumentList.Add($"workMs={(int)work.TotalMilliseconds}");
            var process = Process.Start(start)!;
            process.ErrorDataReceived += (_, _) => { };
            process.BeginErrorReadLine();

            var ready = new TaskCompletionSource();
            var reader = Task.Run(async () =>
            {
                while (await process.StandardOutput.ReadLineAsync() is { } line)
                {
                    if (line == "READY")
                    {
                        ready.TrySetResult();
                    }
                    else if (line.StartsWith("EXEC ", StringComparison.Ordinal))
                    {
                        var parts = line.Split(' ');
                        onExecute(Guid.Parse(parts[1]), parts[2]);
                    }
                }

                ready.TrySetException(new InvalidOperationException("worker ended before it was ready"));
            });
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(60));
            return new ChildReplica(process, reader);
        }

        public void Kill()
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }

        public async ValueTask DisposeAsync()
        {
            Kill();
            await _process.WaitForExitAsync();
            await _reader; // every EXEC line the process wrote before it died is counted
            _process.Dispose();
        }
    }
}
