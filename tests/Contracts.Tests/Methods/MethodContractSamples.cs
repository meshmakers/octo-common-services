using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;

namespace Contracts.Tests.Methods;

/// <summary>
///     Fixed sample messages that are pinned by the golden JSON files in <c>Golden/</c>.
/// </summary>
internal static class MethodContractSamples
{
    public static readonly Guid InvocationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid CorrelationId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly DateTime Deadline = new(2026, 10, 17, 12, 0, 15, DateTimeKind.Utc);

    public static CkMethodRequest Request() => new("meshtest")
    {
        InvocationId = InvocationId,
        CorrelationId = CorrelationId,
        MethodId = "System.Identity/User.ChangePassword-1",
        CkTypeId = "System.Identity/User",
        TargetRtId = "65f0c0ffee00000000000001",
        ParametersJson = "{\"oldValue\":\"a\"}",
        Caller = new CkMethodCaller
        {
            SubjectId = "sub-1", ClientId = "studio", TenantId = "meshtest", Name = "Alice", Roles = ["UserManagement"]
        },
        CallerAccessToken = "jwt",
        DeadlineUtc = Deadline,
        IdempotencyKey = "key-1"
    };

    public static CkMethodResponse Success() =>
        CkMethodResponse.Succeeded(InvocationId, "abc123", "{\"value\":true}");

    public static CkMethodResponse Error() => CkMethodResponse.Failed(InvocationId, "abc123",
        new CkMethodError
        {
            Code = "PASSWORD_TOO_SHORT",
            Parameters = new Dictionary<string, string> { ["minLength"] = "8" },
            Details =
            [
                new CkMethodErrorDetail
                {
                    Code = "REQUIRES_DIGIT", Parameters = new Dictionary<string, string> { ["count"] = "1" }
                }
            ]
        });

    public static CkMethodResponse Accepted() => CkMethodResponse.Accepted(InvocationId, "abc123",
        new CkMethodOperationHandle
        {
            OperationId = "op-1",
            AcceptedAtUtc = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc),
            RetryAfterSeconds = 2,
            ExpiresAtUtc = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc)
        });

    public static CkMethodHandlerAnnounced Announced() =>
        new(CorrelationId, new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc))
        {
            ServiceName = "IdentityService",
            InstanceId = "0123456789abcdef0123456789abcdef",
            MethodIds = ["System.Identity/User.ChangePassword-1"],
            Version = "3.5.0"
        };

    public static CkMethodHandlerWithdrawn Withdrawn() =>
        new(CorrelationId, new DateTime(2026, 10, 17, 12, 5, 0, DateTimeKind.Utc))
        {
            ServiceName = "IdentityService",
            InstanceId = "0123456789abcdef0123456789abcdef",
            MethodIds = ["System.Identity/User.ChangePassword-1"]
        };

    public static CkMethodHandlerDiscoveryRequested Discovery() =>
        new(CorrelationId, new DateTime(2026, 10, 17, 11, 59, 0, DateTimeKind.Utc))
        {
            RequestingInstanceId = "fedcba9876543210fedcba9876543210"
        };
}
