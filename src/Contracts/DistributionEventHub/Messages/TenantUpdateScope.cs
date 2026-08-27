namespace Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;

/// <summary>
///     Declares how invasive a <see cref="PreUpdateTenant" />/<see cref="PosUpdateTenant" /> pair is,
///     so consumers can scale their reaction (AB#4895).
/// </summary>
public enum TenantUpdateScope
{
    /// <summary>
    ///     A full tenant update (CK model import, ClearCache, provisioning). Consumers reload the
    ///     tenant and the communication controller relays an adapter restart. This is the default —
    ///     publishers on an older contract version deserialize to this value.
    /// </summary>
    Full = 0,

    /// <summary>
    ///     Only cached tenant/CK-model state changed (e.g. the nightly autocomplete aggregation
    ///     writing <c>AutoCompleteValues</c> onto CK attributes). Consumers should invalidate their
    ///     caches; the communication controller broadcasts <c>CkModelChanged</c> to adapters instead
    ///     of relaying a full SignalR stop/start — the nightly fleet-wide adapter restart was the
    ///     trigger window for AB#4876.
    /// </summary>
    CacheOnly = 1
}
