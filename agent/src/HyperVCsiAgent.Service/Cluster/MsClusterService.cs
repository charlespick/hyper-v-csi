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
            if (!WqlNames.IsVmId(nodeId))
            {
                // Not null, which this interface reserves for "the cluster has
                // no such VM" - a claim about the cluster, and we have not asked
                // it anything. A node ID that is not a GUID means the node
                // plugin sent something other than its VM ID.
                throw new InvalidOperationException(
                    $"node ID {nodeId} is not a virtual machine GUID, so the cluster cannot be asked about it");
            }

            var scope = new ManagementScope(ScopePath);

            // One indexed lookup, no enumeration and no fan-out. MSCluster_VirtualMachine
            // is the cluster resource's private properties surfaced as a class, so VmID
            // is queryable here in a way it is not on MSCluster_Resource - which is what
            // makes resolving a node by identity as cheap as resolving one by name was,
            // without the naming assumptions that came with the name.
            var query = new ObjectQuery(
                $"SELECT Name, OwnerNode FROM MSCluster_VirtualMachine WHERE VmID = '{nodeId}'");

            using var searcher = new ManagementObjectSearcher(scope, query);
            using var results = searcher.Get();

            foreach (var instance in results)
            {
                using var resource = (ManagementObject)instance;
                cancellationToken.ThrowIfCancellationRequested();

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

    public Task<bool> IsHostLiveAsync(string hostName, CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "node liveness is only needed for forced detach from a failed node, which is not implemented yet; " +
            "an unpublish whose owning host is down fails and is retried rather than fenced");
}
