using Meshmakers.Octo.Services.RestDeprecation;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace Meshmakers.Octo.Services.Swagger.Transformers;

/// <summary>
///     Marks operations whose action carries <see cref="OctoRestDeprecatedAttribute" /> as <c>deprecated: true</c> and
///     appends "Sunset &lt;date&gt;, use &lt;link&gt;." (plus " Successor version: &lt;url&gt;." when set) to their
///     description (AB#8421). Without a sunset date (a wave whose sunset is not configured yet, AB#8415) the notice is
///     "Deprecated, use &lt;link&gt;.". Unmarked operations are not touched.
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

        var resolver = context.ApplicationServices.GetService<RestDeprecationResolver>() ??
                       RestDeprecationResolver.Unconfigured;
        var values = resolver.Resolve(deprecation);

        operation.Deprecated = true;
        var notice = values.SunsetDay != null
            ? $"Sunset {values.SunsetDay}, use {deprecation.Link}."
            : $"Deprecated, use {deprecation.Link}.";
        if (!string.IsNullOrEmpty(deprecation.Successor))
        {
            notice += $" Successor version: {deprecation.Successor}.";
        }

        operation.Description = string.IsNullOrWhiteSpace(operation.Description)
            ? notice
            : operation.Description.TrimEnd() + "\n\n" + notice;
        return Task.CompletedTask;
    }
}
