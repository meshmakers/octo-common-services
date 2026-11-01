using System.Collections.Concurrent;
using System.Reflection;

namespace Meshmakers.Octo.Services.Methods.Handlers;

/// <summary>
///     Recognises an exception after which the writes of a handler may or may not have taken effect (rule R3 of the method
///     consistency model, AB#8020): the caller then gets <c>METHOD_OUTCOME_UNKNOWN</c>, not <c>METHOD_HANDLER_FAILED</c>
///     ("nothing happened").
/// </summary>
/// <remarks>
///     <para>
///         Recognised, anywhere in the chain of inner exceptions (the persistence layer wraps driver exceptions, e.g. into
///         <c>OperationFailedException</c>):
///     </para>
///     <list type="bullet">
///         <item>
///             a write-concern error of MongoDB: <c>MongoWriteConcernException</c> (commands, transaction commit), and
///             <c>MongoWriteException</c> / <c>MongoBulkWriteException</c> that carry a <c>WriteConcernError</c>. The
///             repositories write with <c>w:majority</c> and a <c>wtimeout</c> of 30 s; a write-concern error (the timeout,
///             code 64, or any other) does not undo the write on the primary, it is only not confirmed by a majority;
///         </item>
///         <item>
///             a MongoDB exception with the error label <c>UnknownTransactionCommitResult</c> (the driver sets it when a
///             commit may have succeeded on the server: write-concern error, network error or timeout during the commit).
///         </item>
///     </list>
///     <para>
///         Everything else - including a write error without a write-concern error (the write was refused) - stays
///         <c>METHOD_HANDLER_FAILED</c>. Nothing of the exception reaches the wire either way.
///     </para>
///     <para>
///         The SDK deliberately does not reference the MongoDB driver (a handler never sees a database session, and
///         <c>CkMethodContextSessionFreeTests</c> pins that the assembly has no <c>MongoDB.*</c> reference), so the driver
///         types are recognised by their full names and the public members <c>WriteConcernError</c> and
///         <c>ErrorLabels</c>. The tests build the real driver exceptions, so a rename in a driver update turns them red.
///     </para>
/// </remarks>
internal static class CkMethodWriteOutcome
{
    /// <summary>The driver's label for a commit whose result is unknown.</summary>
    internal const string UnknownTransactionCommitResultLabel = "UnknownTransactionCommitResult";

    private const string DriverNamespacePrefix = "MongoDB.Driver";
    private const string WriteConcernExceptionTypeName = "MongoDB.Driver.MongoWriteConcernException";
    private const string WriteConcernErrorProperty = "WriteConcernError";
    private const string ErrorLabelsProperty = "ErrorLabels";

    // a guard against pathological (cyclic or very deep) exception chains
    private const int MaxDepth = 32;

    private static readonly ConcurrentDictionary<Type, DriverMembers> MembersByType = new();

    /// <summary>
    ///     <c>true</c> when <paramref name="exception" /> (or an exception it wraps) says that a write may have taken effect
    ///     without being acknowledged.
    /// </summary>
    public static bool IsUnknown(Exception? exception)
    {
        var pending = new Stack<(Exception Exception, int Depth)>();
        if (exception is not null)
        {
            pending.Push((exception, 0));
        }

        while (pending.Count > 0)
        {
            var (current, depth) = pending.Pop();
            if (IsUnacknowledgedWrite(current))
            {
                return true;
            }

            if (depth >= MaxDepth)
            {
                continue;
            }

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    pending.Push((inner, depth + 1));
                }
            }
            else if (current.InnerException is { } inner)
            {
                pending.Push((inner, depth + 1));
            }
        }

        return false;
    }

    private static bool IsUnacknowledgedWrite(Exception exception)
    {
        var members = MembersByType.GetOrAdd(exception.GetType(), DriverMembers.Of);
        if (!members.IsDriverException)
        {
            return false;
        }

        if (members.IsWriteConcernException)
        {
            return true;
        }

        try
        {
            if (members.WriteConcernError?.GetValue(exception) is not null)
            {
                return true;
            }

            return members.ErrorLabels?.GetValue(exception) is IEnumerable<string> labels &&
                   labels.Contains(UnknownTransactionCommitResultLabel, StringComparer.Ordinal);
        }
        catch (TargetInvocationException)
        {
            // a getter that throws tells us nothing; the exception stays METHOD_HANDLER_FAILED
            return false;
        }
    }

    private sealed record DriverMembers(
        bool IsDriverException,
        bool IsWriteConcernException,
        PropertyInfo? WriteConcernError,
        PropertyInfo? ErrorLabels)
    {
        private static readonly DriverMembers None = new(false, false, null, null);

        public static DriverMembers Of(Type type)
        {
            var isDriver = false;
            var isWriteConcern = false;
            for (var t = type; t is not null && t != typeof(Exception); t = t.BaseType)
            {
                if (t.Namespace?.StartsWith(DriverNamespacePrefix, StringComparison.Ordinal) == true)
                {
                    isDriver = true;
                }

                if (string.Equals(t.FullName, WriteConcernExceptionTypeName, StringComparison.Ordinal))
                {
                    isWriteConcern = true;
                }
            }

            if (!isDriver)
            {
                return None;
            }

            return new DriverMembers(true, isWriteConcern, Property(type, WriteConcernErrorProperty),
                Property(type, ErrorLabelsProperty));
        }

        // GetProperty(name) throws on a property a derived type hides with "new"; the first match is the most derived one
        private static PropertyInfo? Property(Type type, string name) =>
            type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(p => p.Name == name && p.GetIndexParameters().Length == 0);
    }
}
