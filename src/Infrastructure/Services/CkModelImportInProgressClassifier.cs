namespace Meshmakers.Octo.Services.Infrastructure.Services;

/// <summary>
///     AB#8677: tells a failure that happened only because another service was importing or upgrading a CK model at
///     that moment (the dependency had no Available version for a short time) from a real error. The CK engine marks
///     such a failure with the key <see cref="ExceptionDataKey" /> in <see cref="Exception.Data" />
///     (<c>Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.CkModelImportInProgress.ExceptionDataKey</c>);
///     the key is matched as a string so that this package does not need a newer engine package.
/// </summary>
internal static class CkModelImportInProgressClassifier
{
    /// <summary>Must match <c>CkModelImportInProgress.ExceptionDataKey</c> of the engine (MongoDB).</summary>
    internal const string ExceptionDataKey = "Meshmakers.Octo.CkModelImportInProgress";

    /// <summary>Whether the exception, or an exception it wraps, carries the engine's mark.</summary>
    internal static bool IsCkModelImportInProgress(Exception? exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current.Data.Contains(ExceptionDataKey))
            {
                return true;
            }

            if (current is AggregateException aggregate &&
                aggregate.InnerExceptions.Any(IsCkModelImportInProgress))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The model names the engine named in the mark, for the log.</summary>
    internal static string? DescribeModels(Exception? exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current.Data.Contains(ExceptionDataKey))
            {
                return current.Data[ExceptionDataKey]?.ToString();
            }
        }

        return null;
    }
}
