using System.Management;
using System.Runtime.Versioning;
using HyperVCsiAgent.Core.Cluster;
using HyperVCsiAgent.Service.Cim;

namespace HyperVCsiAgent.Service.Cluster;

/// <summary>
/// Reads ownership out of <c>root\MSCluster</c> on the local host. Local, and
/// still cluster-wide: CLUSDB is replicated to every node, so whichever host
/// currently owns the agent's clustered role answers for the whole cluster with
/// no fan-out.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MsClusterService(ILogger<MsClusterService> logger) : IClusterService
{
    private const string ScopePath = @"\\.\root\MSCluster";

    public Task<ClusteredVm?> ResolveVmAsync(string nodeId, CancellationToken cancellationToken) =>
        // System.Management is entirely synchronous, so the query runs on a
        // pool thread.
        Task.Run<ClusteredVm?>(() =>
        {
            // No longer an injection guard - the node ID is compared in memory
            // below rather than interpolated into the query - but still worth
            // asserting: a node ID that is not a GUID means the node plugin sent
            // something other than its VM ID, and quietly matching nothing would
            // report that as "no such VM in the cluster".
            //
            // Throwing rather than returning null for the same reason: null is
            // this interface's word for a claim about the cluster, and this is a
            // claim about the request.
            if (!WqlNames.IsVmId(nodeId))
            {
                throw new InvalidOperationException(
                    $"node ID {nodeId} is not a virtual machine GUID, so it cannot identify a VM");
            }

            var scope = new ManagementScope(ScopePath);

            // One round trip for every clustered VM resource, then the match in
            // memory. Not a WHERE, because VmID is not a property of
            // MSCluster_Resource at all - it is a member of the embedded
            // PrivateProperties object, and WQL cannot reach inside one. The
            // filtering is local rather than the query being repeated, so this
            // stays a single call to the provider whatever the cluster's size.
            //
            // Both resource types are asked for because a clustered VM has two -
            // "Virtual Machine <name>" and "Virtual Machine Configuration
            // <name>" - and both carry VmID. They live in the same group and so
            // report the same OwnerNode, which is the only thing read off them
            // here, so whichever matches first is as good as the other.
            var query = new ObjectQuery(
                "SELECT * FROM MSCluster_Resource " +
                "WHERE Type = 'Virtual Machine' OR Type = 'Virtual Machine Configuration'");

            using var searcher = new ManagementObjectSearcher(scope, query);
            using var results = searcher.Get();

            foreach (var instance in results)
            {
                using var resource = (ManagementObject)instance;
                cancellationToken.ThrowIfCancellationRequested();

                if (!IsVm(resource, nodeId))
                {
                    continue;
                }

                // OwnerNode is the node currently running the resource - the
                // same answer MSCluster_NodeToActiveResource gives, without the
                // association traversal.
                if (resource["OwnerNode"] as string is not { } owner || string.IsNullOrWhiteSpace(owner))
                {
                    // Not expected to be reachable: a cluster group always has a
                    // current owner, including while offline or failed -
                    // ownership transfers rather than lapsing. Failing loudly
                    // because the alternative reading, "no owner means nothing
                    // is attached", would have a detach report success without
                    // having touched the VM.
                    throw new InvalidOperationException(
                        $"the cluster reports no owning node for VM {nodeId}, which should not be possible");
                }

                logger.LogDebug("VM {VmId} is owned by {OwnerNode}", nodeId, owner);
                return new ClusteredVm(nodeId, owner);
            }

            return null;
        }, cancellationToken);

    /// <summary>
    /// Whether a cluster resource is the VM with this ID, read out of the
    /// embedded PrivateProperties object where clustering keeps a resource's
    /// type-specific settings.
    /// </summary>
    private static bool IsVm(ManagementObject resource, string vmId)
    {
        // A resource whose private properties cannot be read is not a match, but
        // it is also not this method's business to decide what that means: it
        // simply is not the VM we are looking for, and the caller's "no such VM"
        // answer is the honest outcome if none of them are.
        if (resource["PrivateProperties"] is not ManagementBaseObject privateProperties)
        {
            return false;
        }

        using (privateProperties)
        {
            // Braces are stripped because clustering and the guest's key-value
            // pools do not agree on whether to include them, and the comparison
            // is case-insensitive because neither agrees on case either.
            return privateProperties["VmID"] as string is { } candidate
                && string.Equals(candidate.Trim('{', '}'), vmId, StringComparison.OrdinalIgnoreCase);
        }
    }

    public Task<bool> IsHostLiveAsync(string hostName, CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "node liveness is only needed for forced detach from a failed node, which is not implemented yet; " +
            "an unpublish whose owning host is down fails and is retried rather than fenced");
}
