namespace Meshmakers.Octo.Services.Methods;

/// <summary>Registers handlers for CK methods.</summary>
public interface ICkMethodHandlerRegistrationBuilder
{
    /// <summary>
    ///     Registers <typeparamref name="THandler" /> for a method. The handler is resolved from a new DI scope per
    ///     invocation. The method id is the generated constant of the model's <c>*CkIds</c> class, e.g.
    ///     <c>System.Identity/User.ChangePassword-1</c>.
    /// </summary>
    /// <typeparam name="THandler">
    ///     A class implementing exactly one of <see cref="ICkMethodHandler{TParameters}" /> or
    ///     <see cref="ICkMethodHandler{TParameters, TResult}" />.
    /// </typeparam>
    /// <exception cref="ArgumentException">The method id is not a qualified CK method id.</exception>
    /// <exception cref="InvalidOperationException">The method is already registered or the handler type is not typed.</exception>
    ICkMethodHandlerRegistrationBuilder Handle<THandler>(string qualifiedMethodId)
        where THandler : class, ICkMethodHandler;

    /// <summary>
    ///     Registers <typeparamref name="THandler" /> for a method that only this process may call (AB#8411): the handler
    ///     is <b>not</b> announced and gets <b>no</b> bus queue, so a call from another process is answered
    ///     <c>METHOD_HANDLER_NOT_AVAILABLE</c>, while a call through <see cref="ICkMethodDispatcher" /> in this process runs
    ///     it in-process (transport <c>in-process</c>) through the same pipeline, authorization and redaction. Use it for a
    ///     REST facade of a method whose bus exposure is switched off (e.g. password methods before B0). The one-place rule
    ///     stays: a method is registered either for the bus or local only, never both. In
    ///     <c>AddCkInProcessMethodHandlers</c> every registration is local only, so this equals
    ///     <see cref="Handle{THandler}" /> there.
    /// </summary>
    /// <typeparam name="THandler">As for <see cref="Handle{THandler}" />.</typeparam>
    /// <exception cref="ArgumentException">The method id is not a qualified CK method id.</exception>
    /// <exception cref="InvalidOperationException">The method is already registered or the handler type is not typed.</exception>
    ICkMethodHandlerRegistrationBuilder HandleLocalOnly<THandler>(string qualifiedMethodId)
        where THandler : class, ICkMethodHandler;
}
