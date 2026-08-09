using System.Collections.Concurrent;
using HyperVCsiAgent.Core.Configuration;
using Microsoft.Extensions.Options;

namespace HyperVCsiAgent.Core.HostControl;

/// <summary>
/// The single cap on concurrent operations against any one Hyper-V host,
/// shared by every caller that issues one: <see cref="AttachService"/>'s
/// attach and detach, and <see cref="Storage.SnapshotService"/>'s checkpoint
/// take, classify, find and destroy.
/// </summary>
/// <remarks>
/// One shared instance rather than one per caller - issue #14's D4. Checkpoint
/// take, classify, find and destroy are host CIM calls the same as an attach
/// or a detach is, and a merge is heavier than either. Two separate caps, one
/// held by each service, would let twice
/// <see cref="AgentOptions.MaxConcurrentHostOperations"/> operations run
/// against one host's vmms at once - exactly the unbounded concurrency D4
/// describes, just moved one level down instead of closed. Registered as a
/// singleton so both callers resolve the same one, mirroring how
/// <see cref="Storage.SnapshotCopySlots"/> is shared between
/// <see cref="Storage.SnapshotService"/> and <see cref="Storage.VhdxService"/>
/// for the same reason.
/// <para>
/// Keyed by host name, case-insensitively, matching the comparer
/// <see cref="AttachService"/> used on its own <c>_hostConcurrency</c>
/// dictionary before this type existed: Windows host names are not
/// case-sensitive, and a caller that resolves the same host through two
/// different casings still has to share its one cap. A semaphore is created
/// lazily per host on first use rather than for every host in the cluster up
/// front, since most deployments never talk to most hosts from any one agent
/// instance.
/// </para>
/// <para>
/// Deliberately never disposes the semaphores it creates, and deliberately
/// does not implement <see cref="IDisposable"/> at all rather than
/// implementing it as a no-op. A <see cref="SemaphoreSlim"/> holds no
/// unmanaged resource unless its <c>AvailableWaitHandle</c> is touched, which
/// nothing here does, and disposing one during shutdown would race whichever
/// caller's own <c>finally</c> is about to call <see cref="Release"/> on it -
/// the container tearing down this singleton while a job cancelled by the
/// same shutdown is still unwinding would make that call's last act be an
/// <see cref="ObjectDisposedException"/> thrown over the top of its real
/// failure. This is exactly the reasoning <see cref="AttachService.Dispose"/>
/// used to carry when it held these semaphores directly; it moved here with
/// them.
/// </para>
/// </remarks>
public sealed class HostOperationSlots
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _slots = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _limit;

    public HostOperationSlots(IOptions<AgentOptions> options) => _limit = options.Value.MaxConcurrentHostOperations;

    public Task WaitAsync(string hostName, CancellationToken cancellationToken) =>
        _slots.GetOrAdd(hostName, _ => new SemaphoreSlim(_limit)).WaitAsync(cancellationToken);

    public void Release(string hostName) => _slots[hostName].Release();
}
