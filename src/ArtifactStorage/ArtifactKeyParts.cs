namespace Meshmakers.Octo.Services.ArtifactStorage;

/// <summary>
///     The parts of a key that follows the <c>&lt;instancePrefix&gt;/&lt;category&gt;/&lt;tenantId&gt;/&lt;file&gt;</c> layout.
/// </summary>
/// <param name="Category">The category segment, e.g. <see cref="ArtifactCategories.Presweep" />.</param>
/// <param name="TenantId">The lower-cased tenant id.</param>
/// <param name="FileName">The file name segment.</param>
public sealed record ArtifactKeyParts(string Category, string TenantId, string FileName);
