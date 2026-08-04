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

    /// <summary>
    /// The cluster resource type a clustered VM has. Matching on it keeps a
    /// resource of another type in the same group - the Virtual Machine
    /// Configuration resource, most obviously - from being mistaken for the VM.
    /// </summary>
    private const string VirtualMachineResourceType = "Virtual Machine";

    /// <summary>
    /// Clustering names a VM's resource after the VM but does not stop there:
    /// the resource is "Virtual Machine &lt;name&gt;", while the *group* it lives in
    /// is named "&lt;name&gt;" alone. So the node ID is matched against the group,
    /// and the VM's own name is what remains once this prefix is removed.
    /// </summary>
    private const string VirtualMachineResourcePrefix = "Virtual Machine ";

    public Task<ClusteredVm?> ResolveVmAsync(string nodeId, CancellationToken cancellationToken) =>
        // System.Management is entirely synchronous, so the query runs on a
        // pool thread.
        Task.Run<ClusteredVm?>(() =>
        {
            // A name outside this shape names no cluster group, and it is the
            // one piece of caller-supplied text that reaches a WQL query.
            if (!WqlNames.IsSafe(nodeId))
            {
                logger.LogWarning(
                    "node {NodeId} is not a usable cluster group name, so it resolves to no virtual machine", nodeId);
                return null;
            }

            var scope = new ManagementScope(ScopePath);

            // Matching on OwnerGroup rather than on the resource's own name is
            // the whole point: the group is what carries the VM's name, and it
            // is also what fails over. Requiring the resource inside it to be of
            // type Virtual Machine means a group that merely shares a node's
            // name cannot be mistaken for that node's VM.
            var query = new ObjectQuery(
                "SELECT Name, OwnerNode FROM MSCluster_Resource " +
                $"WHERE Type = '{VirtualMachineResourceType}' AND OwnerGroup = '{nodeId}'");

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
                        $"the cluster reports no owning node for the virtual machine in group {nodeId}, which should not be possible");
                }

                return new ClusteredVm(VmNameOf(resource, nodeId), owner);
            }

            return null;
        }, cancellationToken);

    public Task<bool> IsHostLiveAsync(string hostName, CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "node liveness is only needed for forced detach from a failed node, which is not implemented yet; " +
            "an unpublish whose owning host is down fails and is retried rather than fenced");

    /// <summary>
    /// The VM's name on its host, taken from the cluster's own record of it
    /// rather than assumed to be the node ID.
    /// </summary>
    /// <remarks>
    /// Both derivations here are conventions, not guarantees - a resource can be
    /// renamed. The sturdy version reads the resource's VmID private property
    /// and has the host look the VM up by GUID, which needs confirming against a
    /// real cluster before it is worth writing; when it is, it changes this
    /// method and nothing above it.
    /// </remarks>
    private static string VmNameOf(ManagementObject resource, string nodeId)
    {
        if (resource["Name"] as string is not { } resourceName || string.IsNullOrWhiteSpace(resourceName))
        {
            return nodeId;
        }

        return resourceName.StartsWith(VirtualMachineResourcePrefix, StringComparison.OrdinalIgnoreCase)
            ? resourceName[VirtualMachineResourcePrefix.Length..]
            // Renamed away from the default, so the group name is the better
            // remaining guess at what the VM is called.
            : nodeId;
    }
}
