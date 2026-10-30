namespace Meshmakers.Octo.Services.Methods.Audit;

/// <summary>
///     Settings of the CK method audit (section <c>Methods:Audit</c>, AB#6919). By default a failing audit write never fails
///     the method call: the failure is logged and counted (<c>ck.method.audit.failures</c>). Regulated tenants can opt in to
///     the opposite.
/// </summary>
public sealed class CkMethodAuditOptions
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = CkMethodHandlerOptions.SectionName + ":Audit";

    /// <summary>
    ///     When <c>true</c>, an audit event that could not be written fails the call for every tenant. Default <c>false</c>.
    ///     The call may already have been executed: the answer is then <c>METHOD_OUTCOME_UNKNOWN</c>, a refusal stays the
    ///     refusal it was.
    /// </summary>
    public bool FailCallOnAuditFailure { get; set; }

    /// <summary>
    ///     Tenant ids (case-insensitive) for which <see cref="FailCallOnAuditFailure" /> applies although it is off for
    ///     everybody else (regulated tenants).
    /// </summary>
    public IList<string> FailCallOnAuditFailureTenants { get; set; } = [];

    internal bool FailsCallFor(string? tenantId) =>
        FailCallOnAuditFailure ||
        (!string.IsNullOrWhiteSpace(tenantId) &&
         FailCallOnAuditFailureTenants.Any(t => string.Equals(t, tenantId, StringComparison.OrdinalIgnoreCase)));
}
