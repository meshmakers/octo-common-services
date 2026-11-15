using System.Diagnostics.Metrics;
using FakeItEasy;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantLifecycle;
using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Meshmakers.Octo.Services.Infrastructure.TenantGeneration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Infrastructure.Tests.TenantGeneration;

/// <summary>A lifecycle store fake that counts reads and can be switched per tenant (AB#8999 tests).</summary>
internal sealed class CountingLifecycleStore
{
    private readonly Dictionary<string, DateTime?> _createdUtc = new(StringComparer.Ordinal);
    private int _reads;

    public CountingLifecycleStore()
    {
        Store = A.Fake<ITenantLifecycleStore>();
        A.CallTo(() => Store.GetAsync(A<string>._, A<CancellationToken>._))
            .ReturnsLazily(async (string tenantId, CancellationToken _) =>
            {
                Interlocked.Increment(ref _reads);
                if (Gate is { } gate)
                {
                    await gate.Task;
                }

                if (Failure is { } failure)
                {
                    throw failure;
                }

                lock (_createdUtc)
                {
                    return _createdUtc.TryGetValue(tenantId, out var created)
                        ? new TenantLifecycleRecord { TenantId = tenantId, CreatedUtc = created ?? default }
                        : null;
                }
            });
    }

    public ITenantLifecycleStore Store { get; }

    public int Reads => Volatile.Read(ref _reads);

    /// <summary>When set, every read waits for it (concurrency tests).</summary>
    public TaskCompletionSource? Gate { get; set; }

    /// <summary>When set, every read throws it.</summary>
    public Exception? Failure { get; set; }

    /// <summary>Sets the record of a tenant; <c>null</c> = a record without <c>CreatedUtc</c>.</summary>
    public void Set(string tenantId, DateTime? createdUtc)
    {
        lock (_createdUtc)
        {
            _createdUtc[tenantId] = createdUtc;
        }
    }

    public void Remove(string tenantId)
    {
        lock (_createdUtc)
        {
            _createdUtc.Remove(tenantId);
        }
    }
}

/// <summary>The check with its reader over a <see cref="CountingLifecycleStore" />.</summary>
internal sealed class GenerationFixture : IDisposable
{
    public const string SystemTenant = "OctoSystem";

    public GenerationFixture(TenantGenerationEnforcementMode mode = TenantGenerationEnforcementMode.LogOnly,
        TimeSpan? cacheDuration = null, bool withStore = true)
    {
        Options = new TenantAuthorizationOptions
        {
            TenantGenerationEnforcement = mode,
            TenantGenerationCacheDuration = cacheDuration ?? TimeSpan.FromSeconds(30)
        };
        Reader = new TenantGenerationReader(Microsoft.Extensions.Options.Options.Create(Options),
            NullLogger<TenantGenerationReader>.Instance, withStore ? Lifecycle.Store : null, Time);
        Check = new TenantGenerationCheck(Reader, Metrics, Microsoft.Extensions.Options.Options.Create(Options),
            Microsoft.Extensions.Options.Options.Create(new OctoSystemConfiguration { SystemTenantId = SystemTenant }),
            Logger, Time);
    }

    public CountingLifecycleStore Lifecycle { get; } = new();

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));

    public TenantAuthorizationOptions Options { get; }

    public TenantGenerationMetrics Metrics { get; } = new();

    public RecordingLogger<TenantGenerationCheck> Logger { get; } = new();

    public TenantGenerationReader Reader { get; }

    public TenantGenerationCheck Check { get; }

    public void Dispose() => Metrics.Dispose();
}

/// <summary>Collects the measurements of <c>octo.tenant_auth.generation_checks</c> with their tags.</summary>
internal sealed class GenerationCheckListener : IDisposable
{
    private readonly MeterListener _listener = new();

    public GenerationCheckListener(TenantGenerationMetrics metrics)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument.Meter, metrics.Meter) &&
                instrument.Name == TenantGenerationMetrics.CounterName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            var copy = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var tag in tags)
            {
                copy[tag.Key] = tag.Value?.ToString();
            }

            lock (Measurements)
            {
                Measurements.Add((value, copy));
            }
        });
        _listener.Start();
    }

    public List<(long Value, Dictionary<string, string?> Tags)> Measurements { get; } = [];

    public void Dispose() => _listener.Dispose();
}

/// <summary>Records rendered warnings.</summary>
internal sealed class RecordingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
{
    public List<string> Warnings { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
        TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning)
        {
            lock (Warnings)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
