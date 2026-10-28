using System.Text;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;

namespace Meshmakers.Octo.Services.Methods;

/// <summary>
///     Size limit of the parameters of a method call (configuration key <c>Methods:MaxParametersBytes</c>). Read by the
///     gateway (before the parameters are validated) and by the handler host (before they are deserialized); a service
///     that is both uses the one value.
/// </summary>
public sealed class CkMethodLimitsOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = CkMethodHandlerOptions.SectionName;

    /// <summary>The default limit, 1 MiB.</summary>
    public const int DefaultMaxParametersBytes = 1024 * 1024;

    /// <summary>
    ///     Largest accepted UTF-8 byte length of <see cref="CkMethodRequest.ParametersJson" />; exactly this size passes.
    ///     A value that is not positive falls back to <see cref="DefaultMaxParametersBytes" />.
    /// </summary>
    public int MaxParametersBytes { get; set; } = DefaultMaxParametersBytes;

    /// <summary>The effective limit.</summary>
    public int EffectiveMaxParametersBytes => MaxParametersBytes > 0 ? MaxParametersBytes : DefaultMaxParametersBytes;
}

/// <summary>The parameter size check shared by the gateway and the handler host.</summary>
public static class CkMethodParameterLimit
{
    /// <summary>
    ///     Returns whether <paramref name="parametersJson" /> is larger than <paramref name="options" /> allows.
    /// </summary>
    /// <param name="parametersJson">The parameter JSON (null counts as empty).</param>
    /// <param name="options">The limit; null means the default.</param>
    /// <param name="actualBytes">The UTF-8 byte length.</param>
    public static bool Exceeds(string? parametersJson, CkMethodLimitsOptions? options, out long actualBytes)
    {
        var max = (options ?? new CkMethodLimitsOptions()).EffectiveMaxParametersBytes;
        // a char is at most 3 UTF-8 bytes (a surrogate pair is 4 bytes for 2 chars): below a third of the limit no count is needed
        if (parametersJson is null || parametersJson.Length <= max / 3)
        {
            actualBytes = parametersJson?.Length ?? 0;
            return false;
        }

        actualBytes = Encoding.UTF8.GetByteCount(parametersJson);
        return actualBytes > max;
    }

    /// <summary>The <c>METHOD_PARAMETERS_TOO_LARGE</c> error of a call.</summary>
    public static CkMethodError TooLarge(string methodId, CkMethodLimitsOptions? options, long actualBytes) =>
        CkMethodError.Create(CkMethodErrorCodes.MethodParametersTooLarge,
            (CkMethodErrorParameters.MethodId, methodId),
            (CkMethodErrorParameters.MaxBytes, (options ?? new CkMethodLimitsOptions()).EffectiveMaxParametersBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            (CkMethodErrorParameters.ActualBytes, actualBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)));
}
