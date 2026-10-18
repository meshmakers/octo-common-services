namespace Meshmakers.Octo.Services.ArtifactStorage;

/// <summary>
///     The parts of a key that follows the <c>&lt;instancePrefix&gt;/&lt;category&gt;/&lt;tenantId&gt;/&lt;file&gt;</c> layout.
/// </summary>
/// <param name="Category">The category segment, e.g. <see cref="ArtifactCategories.Presweep" />.</param>
/// <param name="TenantId">The lower-cased tenant id.</param>
/// <param name="FileName">The file name segment.</param>
public sealed record ArtifactKeyParts(string Category, string TenantId, string FileName);

/// <summary>
///     The parts of a nested key that follows the
///     <c>&lt;instancePrefix&gt;/&lt;category&gt;/&lt;tenantId&gt;/&lt;runId&gt;/&lt;segment&gt;[/&lt;segment&gt;…]</c> layout.
/// </summary>
/// <param name="Category">The category segment, e.g. <see cref="ArtifactCategories.CopyStaging" />.</param>
/// <param name="TenantId">The lower-cased tenant id.</param>
/// <param name="RunId">The run id segment.</param>
/// <param name="Segments">The remaining segments below the run folder (at least one).</param>
public sealed record ArtifactNestedKeyParts(
    string Category,
    string TenantId,
    string RunId,
    IReadOnlyList<string> Segments)
{
    /// <summary>The remaining segments joined with <c>/</c>.</summary>
    public string RelativePath => string.Join(ArtifactKey.Separator, Segments);
}
