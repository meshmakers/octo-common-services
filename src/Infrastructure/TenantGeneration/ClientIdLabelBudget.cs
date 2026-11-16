namespace Meshmakers.Octo.Services.Infrastructure.TenantGeneration;

/// <summary>
///     Bounds the <c>client_id</c> label of the counters on meter <c>Meshmakers.Octo.TenantAuthorization</c>
///     (<see cref="TenantGenerationMetrics" />, <c>TenantAuthorizationMetrics</c>): a missing or malformed value is
///     <see cref="TenantGenerationMetrics.UnknownClientId" />, and after <c>maxClientIds</c> distinct values per instance
///     further ones are <see cref="TenantGenerationMetrics.OtherClientId" />, so the series stay bounded.
/// </summary>
internal sealed class ClientIdLabelBudget(int maxClientIds)
{
    private readonly HashSet<string> _clientIds = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>The label value of <paramref name="clientId" />.</summary>
    public string Label(string? clientId)
    {
        var bounded = MetricLabels.Bounded(clientId);
        if (bounded is null)
        {
            return TenantGenerationMetrics.UnknownClientId;
        }

        lock (_gate)
        {
            if (_clientIds.Contains(bounded))
            {
                return bounded;
            }

            if (_clientIds.Count >= maxClientIds)
            {
                return TenantGenerationMetrics.OtherClientId;
            }

            _clientIds.Add(bounded);
            return bounded;
        }
    }
}
