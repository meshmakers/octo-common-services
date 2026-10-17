namespace Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;

/// <summary>
///     Version of the CK method wire contract (AB#6874): <see cref="CkMethodRequest" />, <see cref="CkMethodResponse" />
///     and the handler registry events. Rules of evolution are documented in
///     <c>docs/method-wire-contract.md</c>.
/// </summary>
/// <remarks>
///     <para>
///         The version is a single integer, the <b>major</b> version. Additive changes (a new optional member, a new
///         platform error code, a new well-known error parameter) never change it: every reader ignores members it does
///         not know and treats a missing member as its documented default. Only a breaking change (removed or renamed
///         member, changed meaning or type) increments it.
///     </para>
///     <para>
///         A missing <c>contractVersion</c> member on the wire reads as <see cref="CurrentVersion" /> of the first
///         release, i.e. <c>1</c>.
///     </para>
/// </remarks>
public static class CkMethodContract
{
    /// <summary>
    ///     The contract version written by this library and the highest one it understands.
    /// </summary>
    public const int CurrentVersion = 1;

    /// <summary>
    ///     Returns whether a handler that speaks up to <paramref name="supportedVersion" /> can process a message of
    ///     <paramref name="messageVersion" />. Newer messages are rejected with
    ///     <see cref="CkMethodErrorCodes.MethodContractVersionUnsupported" />; older ones are accepted.
    /// </summary>
    /// <param name="messageVersion">Version carried by the message</param>
    /// <param name="supportedVersion">Highest version the reader understands</param>
    /// <returns><c>true</c> when the message can be processed</returns>
    public static bool IsSupported(int messageVersion, int supportedVersion = CurrentVersion) =>
        messageVersion >= 1 && messageVersion <= supportedVersion;
}
