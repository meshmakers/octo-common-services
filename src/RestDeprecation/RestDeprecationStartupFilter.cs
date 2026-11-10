using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Services.RestDeprecation;

/// <summary>
///     Installs <see cref="RestDeprecationMiddleware" /> at the start of the pipeline and validates every
///     <see cref="OctoRestDeprecatedAttribute" /> (and the configuration of its wave) when the pipeline is built, i.e. before the server accepts requests
///     (AB#8421).
/// </summary>
/// <remarks>
///     A startup filter needs no line in the service's <c>Program.cs</c>: <c>AddObservability()</c> registers it, so a
///     service only puts the attribute on the action. An invalid attribute throws here, which fails the host's start
///     with a message naming every offending action.
/// </remarks>
internal sealed class RestDeprecationStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.UseMiddleware<RestDeprecationMiddleware>();
            next(app);

            // After next(app) the application has mapped its endpoints, so the data source knows them all.
            var dataSource = app.ApplicationServices.GetService<EndpointDataSource>();
            if (dataSource != null)
            {
                Validate(dataSource.Endpoints,
                    app.ApplicationServices.GetService<RestDeprecationResolver>() ?? RestDeprecationResolver.Unconfigured);
            }
        };
    }

    /// <summary>
    ///     Throws when an endpoint carries an invalid <see cref="OctoRestDeprecatedAttribute" /> or refers to a wave with an
    ///     invalid configuration (AB#8415).
    /// </summary>
    internal static void Validate(IEnumerable<Endpoint> endpoints, RestDeprecationResolver resolver)
    {
        StringBuilder? errors = null;
        foreach (var endpoint in endpoints)
        {
            var deprecation = endpoint.Metadata.GetMetadata<OctoRestDeprecatedAttribute>();
            var error = deprecation == null ? null : resolver.Validate(deprecation);
            if (error == null)
            {
                continue;
            }

            errors ??= new StringBuilder("Invalid [OctoRestDeprecated] attribute:");
            errors.Append(Environment.NewLine).Append("  ").Append(endpoint.DisplayName ?? "(unnamed endpoint)")
                .Append(": ").Append(error);
        }

        if (errors != null)
        {
            throw new InvalidOperationException(errors.ToString());
        }
    }
}
