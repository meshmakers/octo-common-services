namespace Meshmakers.Octo.Services.Infrastructure.TenantGeneration;

/// <summary>Sanitizes label values of the counters on meter <c>Meshmakers.Octo.TenantAuthorization</c>.</summary>
internal static class MetricLabels
{
    private const int MaxLength = 128;

    /// <summary>
    ///     <paramref name="value" /> when it is 1 to 128 printable ASCII characters without spaces, otherwise
    ///     <c>null</c>.
    /// </summary>
    public static string? Bounded(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxLength)
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
