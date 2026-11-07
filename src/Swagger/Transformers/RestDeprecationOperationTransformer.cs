using Meshmakers.Octo.Services.RestDeprecation;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Meshmakers.Octo.Services.Swagger.Transformers;

/// <summary>
///     Marks operations whose action carries <see cref="OctoRestDeprecatedAttribute" /> as <c>deprecated: true</c> and
///     appends "Sunset &lt;date&gt;, use &lt;link&gt;." to their description (AB#8421). Unmarked operations are not touched.
/// </summary>
internal class RestDeprecationOperationTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        // Last wins: an action attribute overrides one on the controller, as in the endpoint metadata the headers use.
        var deprecation = context.Description.ActionDescriptor.EndpointMetadata
            .OfType<OctoRestDeprecatedAttribute>()
            .LastOrDefault();
        if (deprecation == null)
        {
            return Task.CompletedTask;
        }

        operation.Deprecated = true;
        var notice = $"Sunset {deprecation.Sunset}, use {deprecation.Link}.";
        operation.Description = string.IsNullOrWhiteSpace(operation.Description)
            ? notice
            : operation.Description.TrimEnd() + "\n\n" + notice;
        return Task.CompletedTask;
    }
}
