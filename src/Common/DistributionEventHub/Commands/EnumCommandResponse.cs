namespace Meshmakers.Octo.Services.Common.DistributionEventHub.Commands;

/// <summary>
///     Represents a generic command response, so seen "void" (but execution was successful)
/// </summary>
public record GenericCommandResponse<TEnum> where TEnum : Enum
{
    /// <summary>
    /// The response of the command
    /// </summary>
    public TEnum Response { get; init; } = default!;
}