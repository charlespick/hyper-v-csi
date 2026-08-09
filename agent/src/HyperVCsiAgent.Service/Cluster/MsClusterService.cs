using System.Runtime.Versioning;
using HyperVCsiAgent.Core.Configuration;
using HyperVCsiAgent.Core.Cluster;
using HyperVCsiAgent.Service.Cim;
using Microsoft.Extensions.Options;
using Microsoft.Management.Infrastructure;
using Microsoft.Win32;

namespace HyperVCsiAgent.Service.Cluster;

/// <summary>
/// Reads ownership out of the local node's copy of the cluster database. Local,
/// and still cluster-wide: CLUSDB is replicated to every node, so whichever host
/// currently owns the agent's clustered role answers for the whole cluster with
/// no fan-out.
/// </summary>
/// <remarks>
/// Two steps, because no single lookup answers both halves cheaply.
///
/// The VM ID lives in a cluster resource's private properties, and every way of
/// reaching those through WMI costs a round trip to the cluster service per
/// resource inspected - measured at roughly 8ms each when WQL evaluates a
/// <c>PrivateProperties.VmID</c> predicate, and roughly 19ms each when the
/// resources are enumerated and matched in memory. Either way the cost is
/// per-VM, so a thousand-VM cluster spends seconds resolving one node.
///
/// The same private properties are also mirrored into the local registry, where
/// reading one costs about 0.04ms - no RPC, just a local key read. So the VM ID
/// is matched there, and WMI is asked only for <c>OwnerNode</c>, keyed on the
/// resource name. Name is <c>MSCluster_Resource</c>'s key property, so that is an
/// indexed lookup whose cost does not grow with the cluster.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class MsClusterService : IClusterService
{
    private const string ScopePath = @"\\.\root\MSCluster";
    private const string NamespaceName = @"root\MSCluster";

    /// <summary>
    /// The cluster database's mirror of the resource table, replicated to every
    /// node. Readable by Authenticated Users, so the agent needs no privilege
    /// beyond running on a cluster node.
    /// </summary>
    private const string ResourcesKeyPath = @"Cluster\Resources";

    /// <summary>
    /// The cluster resource type a clustered VM has. Matching on it keeps a
    /// resource of another type in the same group - the Virtual Machine
    /// Configuration resource, most obviously - from being mistaken for the VM.
    /// Both carry the same VmID, so the type is what distinguishes them.
    /// </summary>
    private const string VirtualMachineResourceType = "Virtual Machine";

    private readonly ILogger<MsClusterService> _logger;
    private readonly TimeSpan _hostOperationTimeout;

    public MsClusterService(IOptions<AgentOptions> options, ILogger<MsClusterService> logger)
    {
        _logger = logger;
        _hostOperationTimeout = options.Value.HostOperationTimeout;
    }

    public Task<ClusteredVm?> ResolveVmAsync(string nodeId, CancellationToken cancellationToken) =>
        // System.Management and the registry APIs are entirely synchronous, so
        // the work runs on a pool thread.
        Task.Run<ClusteredVm?>(() =>
        {
            var deadline = CimDeadline.After(_hostOperationTimeout);

            // A node ID that is not a GUID means the node plugin sent something
            // other than its VM ID, and quietly matching nothing would report
            // that as "no such VM in the cluster".
            //
            // Throwing rather than returning null for the same reason: null is
            // this interface's word for a claim about the cluster, and this is a
            // claim about the request.
            if (!WqlNames.IsVmId(nodeId))
            {
                throw new InvalidOperationException(
                    $"node ID {nodeId} is not a virtual machine GUID, so it cannot identify a VM");
            }

            if (FindResourceName(nodeId, cancellationToken) is not { } resourceName)
            {
                return null;
            }

            var owner = ReadOwnerNode(resourceName, deadline, cancellationToken);

            if (string.IsNullOrWhiteSpace(owner))
            {
                // Two readings, both worth failing on. Either the cluster
                // reports no owning node - not expected to be possible, because
                // ownership transfers rather than lapsing, even while a group is
                // offline or failed - or the resource was deleted between the
                // registry read and this query. Returning null would render both
                // as "no such VM", and a detach acting on that would report
                // success without having touched the VM.
                throw new InvalidOperationException(
                    $"the cluster reports no owning node for VM {nodeId} (resource {resourceName}), " +
                    "which should not be possible");
            }

            _logger.LogDebug("VM {VmId} is resource {Resource}, owned by {OwnerNode}", nodeId, resourceName, owner);
            return new ClusteredVm(nodeId, owner);
        }, cancellationToken);

    /// <summary>
    /// Finds the cluster resource for a VM by its ID, returning the resource's
    /// name. Reads the local mirror of the cluster database rather than querying
    /// WMI, because this is the step whose cost would otherwise grow with the
    /// number of VMs in the cluster.
    /// </summary>
    private string? FindResourceName(string vmId, CancellationToken cancellationToken)
    {
        using var resources = Registry.LocalMachine.OpenSubKey(ResourcesKeyPath);
        if (resources is null)
        {
            // Not "no such VM": the cluster database is missing entirely, so
            // this host cannot answer for any VM. Saying so beats reporting
            // every node as absent.
            throw new InvalidOperationException(
                $@"the cluster database is not present at HKLM\{ResourcesKeyPath}; " +
                "is this host a member of a failover cluster and is the cluster service running?");
        }

        // Tracks the first match rather than returning on it, so that a second
        // match - subkey enumeration order under this key is not meaningful,
        // GUID-named subkeys - is not silently picked over. Every other
        // "should not be possible" state below throws rather than guessing;
        // an ambiguous match should not be the exception.
        string? firstMatchResourceName = null;
        string? firstMatchResourceId = null;

        foreach (var resourceId in resources.GetSubKeyNames())
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var resource = resources.OpenSubKey(resourceId);
            if (resource is null)
            {
                // Deleted between listing and opening. Nothing to match.
                continue;
            }

            // Checked before the private properties are opened, so that
            // resources which are not VMs cost one value read rather than two
            // key opens. Most of a real cluster's resources are not VMs.
            if (resource.GetValue("Type") as string is not { } resourceType)
            {
                throw new InvalidOperationException(
                    $"cluster resource {resourceId} has no readable Type value");
            }

            if (resourceType != VirtualMachineResourceType)
            {
                continue;
            }

            using var parameters = resource.OpenSubKey("Parameters");
            if (parameters is null)
            {
                throw new InvalidOperationException(
                    $"cluster VM resource {resourceId} has no Parameters key");
            }

            if (parameters.GetValue("VmID") as string is not { } candidate)
            {
                throw new InvalidOperationException(
                    $"cluster VM resource {resourceId} has no readable VmID value");
            }

            // Braces are stripped because clustering and the guest's key-value
            // pools do not agree on whether to include them, and the comparison
            // is case-insensitive because neither agrees on case either. Doing
            // this in memory is also why the match is not a WQL predicate: WQL
            // compares case-insensitively but does not tolerate braces, so a
            // braced value in the database would silently match nothing.
            if (!string.Equals(candidate.Trim('{', '}'), vmId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (resource.GetValue("Name") as string is not { } resourceName || string.IsNullOrWhiteSpace(resourceName))
            {
                throw new InvalidOperationException(
                    $"cluster VM resource {resourceId} matched VmID {vmId} but has no readable Name value");
            }

            if (firstMatchResourceName is not null)
            {
                throw new InvalidOperationException(
                    $"cluster VM resources {firstMatchResourceName} ({firstMatchResourceId}) and {resourceName} " +
                    $"({resourceId}) both have VmID {vmId}, which should not be possible");
            }

            firstMatchResourceName = resourceName;
            firstMatchResourceId = resourceId;
        }

        return firstMatchResourceName;
    }

    /// <summary>
    /// Reads which node currently runs a resource. OwnerNode is the node running
    /// it - the same answer MSCluster_NodeToActiveResource gives, without the
    /// association traversal - and is live state, which is why it comes from WMI
    /// rather than from the registry that supplied the name.
    /// </summary>
    private static string? ReadOwnerNode(string resourceName, CimDeadline deadline, CancellationToken cancellationToken)
    {
        // Keyed on Name, which is MSCluster_Resource's key property, so this
        // does not scan. The name comes from the cluster database rather than
        // from a caller, but it is still escaped: a resource named with an
        // apostrophe would otherwise produce a malformed query.
        var query =
            $"SELECT Name, OwnerNode FROM MSCluster_Resource WHERE Name = '{WqlNames.EscapeLiteral(resourceName)}'";

        using var session = CimSession.Create(null);
        var options = deadline.Options("reading MSCluster_Resource.OwnerNode", cancellationToken);
        foreach (var resource in session.QueryInstances(NamespaceName, "WQL", query, options))
        {
            return resource.CimInstanceProperties["OwnerNode"]?.Value as string;
        }

        return null;
    }

    public Task<bool> IsHostLiveAsync(string hostName, CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "node liveness is only needed for forced detach from a failed node, which is not implemented yet; " +
            "an unpublish whose owning host is down fails and is retried rather than fenced");

    public Task<IReadOnlyList<string>> ListHostNamesAsync(CancellationToken cancellationToken) =>
        // Same synchronous-API trade every other method here makes: WMI has
        // no async surface, so the call runs on a pool thread.
        Task.Run<IReadOnlyList<string>>(() =>
        {
            var deadline = CimDeadline.After(_hostOperationTimeout);
            var names = new List<string>();

            using var session = CimSession.Create(null);
            var options = deadline.Options("reading MSCluster_Node.Name", cancellationToken);
            foreach (var node in session.QueryInstances(NamespaceName, "WQL", "SELECT Name FROM MSCluster_Node", options))
            {
                if (node.CimInstanceProperties["Name"]?.Value as string is { Length: > 0 } name)
                {
                    names.Add(name);
                }
            }

            if (names.Count == 0)
            {
                // Not "an empty cluster": a caller sweeping for owned
                // checkpoints across every host would read that as "nothing
                // to sweep" and quietly skip every VM on the cluster. A
                // cluster this agent is deployed against has at least the
                // node it is running on, so no nodes back means the query
                // could not be answered - the same reading FindResourceName
                // gives a missing cluster database, and for the same reason:
                // an unanswerable question must not be mistaken for an
                // answer of zero.
                throw new InvalidOperationException(
                    "MSCluster_Node reported no nodes at all, which should not be possible for a cluster this agent is deployed against");
            }

            return names;
        }, cancellationToken);
}
