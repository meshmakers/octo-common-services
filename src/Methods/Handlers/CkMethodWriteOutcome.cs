using Meshmakers.Octo.Runtime.Contracts;

namespace Meshmakers.Octo.Services.Methods.Handlers;

/// <summary>
///     Recognises an exception after which the writes of a handler may or may not have taken effect (rule R3 of the method
///     consistency model, AB#8020): the caller then gets <c>METHOD_OUTCOME_UNKNOWN</c>, not <c>METHOD_HANDLER_FAILED</c>
///     ("nothing happened").
/// </summary>
/// <remarks>
///     <para>
///         A facade over the one classification of unacknowledged writes in the engine,
///         <see cref="UnacknowledgedWriteClassifier" /> (<c>Runtime.Contracts</c>, AB#8037): a MongoDB write-concern error
///         (<c>MongoWriteConcernException</c>, or <c>MongoWriteException</c> / <c>MongoBulkWriteException</c> with a
///         <c>WriteConcernError</c>) or the error label <c>UnknownTransactionCommitResult</c>, anywhere in the chain of inner
///         exceptions. The pipeline hosts (mesh adapter, SDK adapters) use the same classifier and report it as
///         <c>PipelineExecutionFailureKind.OutcomeUnknown</c>, so a pipeline method answers like a service method.
///     </para>
///     <para>
///         Everything else - including a write error without a write-concern error (the write was refused) - stays
///         <c>METHOD_HANDLER_FAILED</c>. Nothing of the exception reaches the wire either way. Neither this assembly nor the
///         engine contracts reference the MongoDB driver (<c>CkMethodContextSessionFreeTests</c>).
///     </para>
/// </remarks>
internal static class CkMethodWriteOutcome
{
    /// <summary>
    ///     <c>true</c> when <paramref name="exception" /> (or an exception it wraps) says that a write may have taken effect
    ///     without being acknowledged.
    /// </summary>
    public static bool IsUnknown(Exception? exception) => UnacknowledgedWriteClassifier.IsOutcomeUnknown(exception);
}
