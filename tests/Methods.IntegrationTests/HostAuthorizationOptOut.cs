using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Services.Methods.Authorization;

namespace Meshmakers.Octo.Services.Methods.IntegrationTests;

/// <summary>
///     For bus tests that are about transport, idempotency, queues, redaction or the gateway pipeline and run a handler host
///     without caller token validation: the host enforces the declared authorization (AB#7664), which these tests do not
///     exercise. Registered before <c>AddCkMethodHandlers</c>, it replaces the shared evaluator. The authorization rules
///     of the host are covered by the unit tests (<c>CkMethodHostAuthorizationTests</c>) and the adversarial suite (C16).
/// </summary>
internal sealed class AllowAnyCallerEvaluator : ICkMethodAuthorizationEvaluator
{
    public CkMethodAuthorizationDecision Evaluate(CkMethodAuthorizationDto? declaration, CkMethodKindDto kind,
        string? targetRtId, CkMethodPrincipal? principal) => CkMethodAuthorizationDecision.Allow;
}
