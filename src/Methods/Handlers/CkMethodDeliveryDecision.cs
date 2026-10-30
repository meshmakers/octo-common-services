namespace Meshmakers.Octo.Services.Methods.Handlers;

/// <summary>
///     Verdict of <see cref="CkMethodSeenInvocations.Decide" /> for one delivered <c>CkMethodRequest</c>.
/// </summary>
public enum CkMethodDeliveryDecision
{
    /// <summary>The invocation id is new and the transport does not flag a duplicate: the handler may run.</summary>
    Fresh = 0,

    /// <summary>
    ///     The transport flags the delivery as a possible duplicate (broker redelivery, bus retry). The previous delivery may
    ///     have run the method: the handler must not run, the caller gets <c>METHOD_OUTCOME_UNKNOWN</c>.
    /// </summary>
    PossibleDuplicate = 1,

    /// <summary>
    ///     The transport raises no flag, but the invocation id was seen before (a caller re-sent the request). The handler
    ///     must not run, the caller gets <c>METHOD_OUTCOME_UNKNOWN</c>.
    /// </summary>
    AlreadySeen = 2
}
