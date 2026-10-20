using System.Collections;
using System.Reflection;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods.Handlers;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.Tests.Typed;

/// <summary>
///     Rule 1 of the method consistency model: a handler never joins the request's transaction, so the method context and
///     the handler abstraction expose no database session or transaction. This test walks the public surface by
///     reflection and fails as soon as a member (or any type reachable from one) is a session or transaction type, so a
///     later "convenient" addition such as <c>IClientSessionHandle Session</c> cannot slip in unnoticed.
/// </summary>
public class CkMethodContextSessionFreeTests
{
    private static readonly string[] ForbiddenTypeNameParts = ["Session", "Transaction"];

    private static readonly string[] ForbiddenNamespacePrefixes = ["MongoDB."];

    private static readonly Type[] Roots =
    [
        typeof(CkMethodContext),
        typeof(CkMethodContext<>),
        typeof(ICkMethodHandler),
        typeof(ICkMethodHandler<>),
        typeof(ICkMethodHandler<,>),
        typeof(ICkMethodHandlerInvoker),
        typeof(CkMethodOutcome),
        typeof(CkMethodOutcome<>),
        typeof(CkMethodRequest),
        typeof(CkMethodResponse)
    ];

    [Fact]
    public void MethodContextAndHandlerAbstraction_ExposeNoSessionOrTransactionType()
    {
        var violations = FindViolations(Roots);

        Assert.True(violations.Count == 0,
            "The method context and the handler abstraction must not expose a database session or transaction " +
            "(a method runs outside the request transaction). Offending members:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void TheMethodsAssembly_DoesNotReferenceTheMongoDriver()
    {
        var references = typeof(CkMethodContext).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToList();

        Assert.DoesNotContain(references, n => n != null && n.StartsWith("MongoDB.", StringComparison.Ordinal));
    }

    // ---- the detector itself must be able to fail ------------------------------------------------------------

    [Fact]
    public void Detector_FlagsASessionPropertyOnAContext()
    {
        var violations = FindViolations([typeof(FakeContextA)]);

        Assert.Contains(violations, v => v.Contains(nameof(FakeContextA.Session), StringComparison.Ordinal));
    }

    [Fact]
    public void Detector_FlagsASessionTypeBehindAGenericOrMethodParameter()
    {
        var violations = FindViolations([typeof(FakeHandlerC), typeof(FakeContextB)]);

        Assert.Contains(violations, v => v.Contains("HandleAsync", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("Inner", StringComparison.Ordinal));
    }

    [Fact]
    public void Detector_FlagsAMongoNamespaceType()
    {
        Assert.NotEmpty(FindViolations([typeof(FakeContextD)]));
    }

    [Fact]
    public void Detector_AcceptsTheCleanContextSurface()
    {
        Assert.Empty(FindViolations([typeof(CleanContext)]));
    }

    private static List<string> FindViolations(IEnumerable<Type> roots)
    {
        var violations = new List<string>();
        var visited = new HashSet<Type>();
        foreach (var root in roots)
        {
            Walk(root, root.Name, violations, visited, 0);
        }

        return violations;
    }

    private static void Walk(Type type, string path, List<string> violations, HashSet<Type> visited, int depth)
    {
        foreach (var inner in Unwrap(type))
        {
            if (IsForbidden(inner))
            {
                violations.Add($"{path} -> {inner.FullName ?? inner.Name}");
                continue;
            }

            // Only our own types are walked further; walking the BCL would only add noise.
            if (depth > 6 || !IsOwnType(inner) || !visited.Add(inner))
            {
                continue;
            }

            foreach (var member in inner.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static |
                                                    BindingFlags.DeclaredOnly))
            {
                var memberPath = $"{path}.{member.Name}";
                if (member is not (PropertyInfo or FieldInfo or MethodBase or EventInfo))
                {
                    continue;
                }

                if (IsNameForbidden(member.Name) && member is PropertyInfo or FieldInfo)
                {
                    violations.Add($"{memberPath} (member name)");
                }

                foreach (var memberType in TypesOf(member))
                {
                    Walk(memberType, memberPath, violations, visited, depth + 1);
                }
            }
        }
    }

    private static IEnumerable<Type> TypesOf(MemberInfo member)
    {
        switch (member)
        {
            case PropertyInfo p:
                yield return p.PropertyType;
                foreach (var parameter in p.GetIndexParameters())
                {
                    yield return parameter.ParameterType;
                }

                break;
            case FieldInfo f:
                yield return f.FieldType;
                break;
            case EventInfo e when e.EventHandlerType != null:
                yield return e.EventHandlerType;
                break;
            case MethodBase m:
                if (m is MethodInfo mi)
                {
                    yield return mi.ReturnType;
                }

                foreach (var parameter in m.GetParameters())
                {
                    yield return parameter.ParameterType;
                }

                break;
        }
    }

    /// <summary>Element, generic argument and array/ref types of a type, including the type itself.</summary>
    private static IEnumerable<Type> Unwrap(Type type)
    {
        if (type.HasElementType)
        {
            foreach (var inner in Unwrap(type.GetElementType()!))
            {
                yield return inner;
            }

            yield break;
        }

        yield return type;
        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments())
            {
                foreach (var inner in Unwrap(argument))
                {
                    yield return inner;
                }
            }
        }
    }

    private static bool IsOwnType(Type type) =>
        type.Namespace != null && type.Namespace.StartsWith("Meshmakers.", StringComparison.Ordinal) &&
        !type.IsGenericParameter;

    private static bool IsForbidden(Type type) =>
        !type.IsGenericParameter &&
        (IsNameForbidden(type.Name) ||
         (type.Namespace != null &&
          ForbiddenNamespacePrefixes.Any(p => type.Namespace.StartsWith(p, StringComparison.Ordinal))));

    private static bool IsNameForbidden(string name) =>
        ForbiddenTypeNameParts.Any(p => name.Contains(p, StringComparison.OrdinalIgnoreCase));

    // ---- fakes used only to prove that the detector fails ----------------------------------------------------

    private interface IClientSessionHandle;

    private sealed class FakeContextA
    {
        public IClientSessionHandle? Session { get; init; }
    }

    private sealed class DbTransaction;

    private sealed class FakeContextB
    {
        public IReadOnlyList<DbTransaction> Inner { get; init; } = [];
    }

    private sealed class FakeHandlerC
    {
        public Task HandleAsync(IClientSessionHandle session, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class FakeContextD
    {
        public MongoDB.Fake.Cursor? Cursor { get; init; }
    }

    private sealed class CleanContext
    {
        public string TenantId { get; init; } = "";

        public IEnumerable Items { get; init; } = Array.Empty<string>();
    }
}
