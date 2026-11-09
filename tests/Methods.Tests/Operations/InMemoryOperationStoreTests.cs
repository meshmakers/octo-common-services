using Meshmakers.Octo.Services.Methods.Operations;

namespace Meshmakers.Octo.Services.Methods.Tests.Operations;

/// <summary>AB#8443: the store contract against <see cref="CkMethodInMemoryOperationStore" />.</summary>
public class InMemoryOperationStoreTests : OperationStoreContract
{
    protected override Task<ICkMethodOperationStore> CreateStoreAsync() =>
        Task.FromResult<ICkMethodOperationStore>(new CkMethodInMemoryOperationStore(Time));
}
