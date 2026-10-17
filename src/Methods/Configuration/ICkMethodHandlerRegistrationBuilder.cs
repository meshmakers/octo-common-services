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
}
