using System.Reflection;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Services.Methods.Handlers;

/// <summary>The handler registrations of one service, collected while the service collection is configured.</summary>
internal sealed class CkMethodHandlerRegistrations
{
    private readonly Dictionary<string, CkMethodRegistration> _registrations = new(StringComparer.Ordinal);

    public string? ServiceName { get; private set; }

    public IReadOnlyDictionary<string, CkMethodRegistration> Handlers => _registrations;

    public void SetServiceName(string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        if (ServiceName != null && !string.Equals(ServiceName, serviceName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"AddCkMethodHandlers was called with different service names ('{ServiceName}', '{serviceName}').");
        }

        ServiceName = serviceName;
    }

    public CkMethodRegistration Add(string qualifiedMethodId, Type handlerType)
    {
        var registration = CkMethodRegistration.Create(qualifiedMethodId, handlerType);
        if (!_registrations.TryAdd(qualifiedMethodId, registration))
        {
            throw new InvalidOperationException(
                $"A handler for CK method '{qualifiedMethodId}' is already registered ({_registrations[qualifiedMethodId].HandlerType.Name}).");
        }

        return registration;
    }
}

/// <summary>Identity and life cycle state of this handler instance (announce, heartbeat, withdraw).</summary>
internal sealed class CkMethodHandlerInstance(
    CkMethodHandlerRegistrations registrations,
    IOptions<CkMethodHandlerOptions> options,
    TimeProvider timeProvider)
{
    private readonly List<EndpointHandle> _endpointHandles = [];
    private readonly Lock _lock = new();

    public string InstanceId { get; } = Guid.NewGuid().ToString("N");

    public string ServiceName => registrations.ServiceName ?? "unknown";

    public IReadOnlyList<string> MethodIds { get; } = registrations.Handlers.Keys.Order(StringComparer.Ordinal).ToList();

    public string? Version { get; } =
        (Assembly.GetEntryAssembly() ?? typeof(CkMethodHandlerInstance).Assembly)
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    /// <summary>Completed when all endpoints are ready and the first announcement is sent.</summary>
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool Withdrawn { get; private set; }

    public void AddEndpointHandle(EndpointHandle handle)
    {
        lock (_lock)
        {
            _endpointHandles.Add(handle);
        }
    }

    public void MarkWithdrawn()
    {
        lock (_lock)
        {
            Withdrawn = true;
        }
    }

    public IReadOnlyList<EndpointHandle> TakeEndpointHandles()
    {
        lock (_lock)
        {
            Withdrawn = true;
            var handles = _endpointHandles.ToList();
            _endpointHandles.Clear();
            return handles;
        }
    }

    public CkMethodHandlerAnnounced CreateAnnouncement()
    {
        return new CkMethodHandlerAnnounced(Guid.NewGuid(), timeProvider.GetUtcNow().UtcDateTime)
        {
            ServiceName = ServiceName,
            InstanceId = InstanceId,
            MethodIds = MethodIds,
            Tenants = [CkMethodHandlerTenants.All],
            Version = Version,
            HeartbeatIntervalSeconds = (int)Math.Ceiling(options.Value.HeartbeatInterval.TotalSeconds),
            TtlSeconds = (int)Math.Ceiling(options.Value.Ttl.TotalSeconds)
        };
    }

    public CkMethodHandlerWithdrawn CreateWithdrawal()
    {
        return new CkMethodHandlerWithdrawn(Guid.NewGuid(), timeProvider.GetUtcNow().UtcDateTime)
        {
            ServiceName = ServiceName,
            InstanceId = InstanceId,
            MethodIds = MethodIds
        };
    }
}
