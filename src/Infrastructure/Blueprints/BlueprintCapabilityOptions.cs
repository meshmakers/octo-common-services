namespace Meshmakers.Octo.Services.Infrastructure.Blueprints;

/// <summary>
///     Timing of <see cref="BlueprintCapabilityReconciler" />. The defaults cover a tenant created with a
///     blueprint: the event is published before the tenant registry commits, so the tenant and its installation row may
///     be visible only a few seconds later; AI waits for Communication, which another service enables.
/// </summary>
public class BlueprintCapabilityOptions
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "BlueprintCapabilities";

    /// <summary>How often the tenant and the installation row are looked up before the event is refused.</summary>
    public int LookupAttempts { get; set; } = 12;

    /// <summary>Wait between two lookups. A negative value counts as zero.</summary>
    public TimeSpan LookupDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>AI only: how often the Communication flag of the tenant is read before AI gives up.</summary>
    public int PrerequisiteAttempts { get; set; } = 36;

    /// <summary>AI only: wait between two reads of the Communication flag. A negative value counts as zero.</summary>
    public TimeSpan PrerequisiteDelay { get; set; } = TimeSpan.FromSeconds(5);
}
