namespace Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;

/// <summary>
///     Represents a command response with an enum result
/// </summary>
public record EnumCommandResponse<TEnum> where TEnum : Enum
{
    /// <summary>
    /// The response of the command
    /// </summary>
    public TEnum Response { get; init; } = default!;
}