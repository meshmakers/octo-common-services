namespace Meshmakers.Octo.Services.Contracts.ApiErrors;

/// <summary>
///     Implements optional details to the failed operation
/// </summary>
public class FailedDetails
{
    /// <summary>
    ///     The message code
    /// </summary>
    public string? Code { get; set; }

    /// <summary>
    ///     Description to the code
    /// </summary>
    public string? Description { get; set; }
}