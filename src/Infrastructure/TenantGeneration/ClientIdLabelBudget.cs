namespace Meshmakers.Octo.Services.Infrastructure.TenantGeneration;

/// <summary>
///     Bounds the <c>client_id</c> label of the counters on meter <c>Meshmakers.Octo.TenantAuthorization</c>
///     (<see cref="TenantGenerationMetrics" />, <c>TenantAuthorizationMetrics</c>): a missing or malformed value is
///     <see cref="TenantGenerationMetrics.UnknownClientId" />, and after <c>maxClientIds</c> distinct values per instance
///     further ones are <see cref="TenantGenerationMetrics.OtherClientId" />, so the series stay bounded.
/// </summary>
internal sealed class ClientIdLabelBudget(int maxClientIds)
{
    private const int MaxLabelLength = 128;

    private readonly HashSet<string> _clientIds = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>The label value of <paramref name="clientId" />.</summary>
    public string Label(string? clientId)
    {
        var bounded = Bounded(clientId);
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

    /// <summary>
    ///     <paramref name="value" /> when it is 1 to 128 printable ASCII characters without spaces, otherwise
    ///     <c>null</c>.
    /// </summary>
    public static string? Bounded(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxLabelLength)
        {
            return null;
        }

        foreach (var c in value)
        {
            if (c < 0x21 || c > 0x7e)
            {
                return null;
            }
        }

        return value;
    }
}
