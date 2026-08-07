using HyperVCsiAgent.Core.Configuration;
using Microsoft.Extensions.Options;

namespace HyperVCsiAgent.Core.Storage;

/// <summary>
/// The single cap on concurrent bulk copies against the CSV, shared by every
/// caller that runs one: <see cref="SnapshotService"/> copying a volume into a
/// snapshot, and <see cref="VhdxService"/> copying a snapshot into a restored
/// volume.
/// </summary>
/// <remarks>
/// One shared instance rather than one per caller. Two separate semaphores each
/// sized at <see cref="AgentOptions.MaxConcurrentSnapshotCopies"/> would let
/// twice that many multi-hour copies run against the CSV at once, which is
/// exactly the throughput problem the setting exists to bound - see
/// <see cref="AgentOptions.MaxConcurrentSnapshotCopies"/> for why it is kept
/// apart from <see cref="AgentOptions.MaxConcurrentDiskOperations"/> in the
/// first place. Registered as a singleton so both callers resolve the same one.
/// </remarks>
public sealed class SnapshotCopySlots : IDisposable
{
    private readonly SemaphoreSlim _semaphore;

    public SnapshotCopySlots(IOptions<AgentOptions> options) =>
        _semaphore = new SemaphoreSlim(options.Value.MaxConcurrentSnapshotCopies);

    public Task WaitAsync(CancellationToken cancellationToken) => _semaphore.WaitAsync(cancellationToken);

    public void Release() => _semaphore.Release();

    public void Dispose() => _semaphore.Dispose();
}
