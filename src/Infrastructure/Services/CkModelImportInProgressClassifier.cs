namespace Meshmakers.Octo.Services.Infrastructure.Services;

/// <summary>
///     AB#8677: tells a failure that happened only because another service was importing or upgrading a CK model at
///     that moment (the dependency had no Available version for a short time) from a real error. The CK engine marks
///     such a failure with the key <see cref="ExceptionDataKey" /> in <see cref="Exception.Data" />
///     (<c>Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.CkModelImportInProgress.ExceptionDataKey</c>);
///     the key is matched as a string so that this package does not need a newer engine package.
///     <para>
///         The same holds for a timeout while waiting for the engine's CK model import lock: another service imports
///         the model and holds the lock (lock id <c>ck_model_import_&lt;model&gt;</c>, taken in
///         <c>DatabaseCkModelRepository.ExecuteImport</c>) longer than the engine waits for it (about 90 s,
///         <c>RepositoryDistributedLockService.AcquireLockAsync</c>). The engine throws a plain
///         <see cref="TimeoutException" /> without the mark there; it is recognised by its source assembly
///         <see cref="EngineAssemblyName" /> and the message prefix <see cref="ImportLockTimeoutMessagePrefix" />.
///         Every other <see cref="TimeoutException" /> stays a real error.
///     </para>
/// </summary>
internal static class CkModelImportInProgressClassifier
{
    /// <summary>
    ///     Must match the engine constant
    ///     <c>Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.CkModelImportInProgress.ExceptionDataKey</c>
    ///     (octo-construction-kit-engine-mongodb). Kept as a string literal on purpose: this package must not depend
    ///     on the engine package.
    /// </summary>
    internal const string ExceptionDataKey = "Meshmakers.Octo.CkModelImportInProgress";

    /// <summary>The assembly that throws the import-lock timeout (<see cref="Exception.Source" />).</summary>
    internal const string EngineAssemblyName = "Meshmakers.Octo.Runtime.Engine.MongoDb";

    /// <summary>
    ///     Start of the message of the engine's lock-wait timeout for a CK model import lock
    ///     (<c>Could not acquire distributed lock 'ck_model_import_&lt;model&gt;' after …</c>); the model name follows up to
    ///     the closing quote.
    /// </summary>
    internal const string ImportLockTimeoutMessagePrefix = "Could not acquire distributed lock 'ck_model_import_";

    /// <summary>Whether the exception, or an exception it wraps, carries the engine's mark.</summary>
    internal static bool IsCkModelImportInProgress(Exception? exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current.Data.Contains(ExceptionDataKey) || ImportLockTimeoutModel(current) != null)
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

    /// <summary>The model names the engine named in the mark, for the log (also inside an <see cref="AggregateException" />).</summary>
    internal static string? DescribeModels(Exception? exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current.Data.Contains(ExceptionDataKey))
            {
                return current.Data[ExceptionDataKey]?.ToString();
            }

            var lockModel = ImportLockTimeoutModel(current);
            if (lockModel != null)
            {
                return lockModel;
            }

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    var models = DescribeModels(inner);
                    if (models != null)
                    {
                        return models;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    ///     The model name when <paramref name="exception" /> is the engine's timeout while waiting for a CK model import
    ///     lock; otherwise <c>null</c>.
    /// </summary>
    private static string? ImportLockTimeoutModel(Exception exception)
    {
        if (exception is not TimeoutException ||
            !string.Equals(exception.Source, EngineAssemblyName, StringComparison.Ordinal) ||
            !exception.Message.StartsWith(ImportLockTimeoutMessagePrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var start = ImportLockTimeoutMessagePrefix.Length;
        var end = exception.Message.IndexOf('\'', start);
        return end > start ? exception.Message[start..end] : null;
    }
}
