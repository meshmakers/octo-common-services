using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Meshmakers.Octo.Services.Infrastructure.TenantGeneration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Services.Methods.Tests.Authorization;

/// <summary>
///     The tenant estate of the parent-tenant administration tests: the system tenant <see cref="SystemTenant" /> above everything,
///     the parent <see cref="Parent" /> with the children <see cref="Child" /> and <see cref="Sibling" />, the grandchild
///     <see cref="Grandchild" /> below <see cref="Child" />, and <see cref="Unrelated" />, a tenant that is nobody's child.
/// </summary>
internal static class ParentTenantAdministrationKit
{
    public const string SystemTenant = "octosystem";
    public const string Parent = "parent1";
    public const string Child = "child1";
    public const string Sibling = "child2";
    public const string Grandchild = "grand1";
    public const string Unrelated = "unrelated1";

    public const string CurrentGeneration = "1794750381540";
    public const string OldGeneration = "1794750081540";

    public const string CrossClient = "platform-worker";

    /// <summary>The registry: one level per ordinary tenant, the whole estate for the system tenant.</summary>
    public sealed class Hierarchy : ITenantHierarchyReader
    {
        private static readonly Dictionary<string, string[]> Children = new(StringComparer.OrdinalIgnoreCase)
        {
            [SystemTenant] = [Parent, Child, Sibling, Grandchild, Unrelated],
            [Parent] = [Child, Sibling],
            [Child] = [Grandchild]
        };

        private int _reads;

        public int Reads => _reads;

        public bool Throws { get; init; }

        public Task<bool> IsChildTenantAsync(string parentTenantId, string tenantId)
        {
            Interlocked.Increment(ref _reads);
            if (Throws)
            {
                throw new InvalidOperationException("registry down");
            }

            if (string.Equals(parentTenantId, tenantId, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(false);
            }

            return Task.FromResult(Children.TryGetValue(parentTenantId, out var children) &&
                                   children.Contains(tenantId, StringComparer.OrdinalIgnoreCase));
        }
    }

    /// <summary>Options with the allow-listed cross-tenant client and every mode enforcing.</summary>
    public static TenantAuthorizationOptions EnforcingOptions()
    {
        var options = new TenantAuthorizationOptions
        {
            TenantGenerationEnforcement = TenantGenerationEnforcementMode.Enforce,
            ServiceTokenEnforcement = ServiceTokenTenantEnforcementMode.Enforce,
            UserTokenEnforcement = UserTokenTenantEnforcementMode.Enforce
        };
        options.CrossTenantServiceClientIds.Add(CrossClient);
        return options;
    }

    /// <summary>A real generation check over a reader that knows the current generation of every tenant.</summary>
    public static ITenantGenerationCheck GenerationCheck(TenantAuthorizationOptions options) =>
        new TenantGenerationCheck(new GenerationReader(), new TenantGenerationMetrics(), Options.Create(options),
            Options.Create(new OctoSystemConfiguration { SystemTenantId = SystemTenant }),
            NullLogger<TenantGenerationCheck>.Instance);

    private sealed class GenerationReader : ITenantGenerationReader
    {
        public ValueTask<string?> GetGenerationAsync(string tenantId, bool refresh = false,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<string?>(CurrentGeneration);

        public void Invalidate(string tenantId)
        {
        }
    }
}
