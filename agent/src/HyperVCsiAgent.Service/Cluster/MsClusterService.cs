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
        // Tracks the first match rather than returning on it, so that a second
        // match - subkey enumeration order under this key is not meaningful,
        // GUID-named subkeys - is not silently picked over. Every other
        // "should not be possible" state below throws rather than guessing;
        // an ambiguous match should not be the exception.
        string? firstMatchResourceName = null;
        string? firstMatchResourceId = null;

        foreach (var (resourceId, resourceName, candidateVmId) in EnumerateVmResources(cancellationToken))
        {
            // Braces are stripped because clustering and the guest's key-value
            // pools do not agree on whether to include them, and the comparison
            // is case-insensitive because neither agrees on case either. Doing
            // this in memory is also why the match is not a WQL predicate: WQL
            // compares case-insensitively but does not tolerate braces, so a
            // braced value in the database would silently match nothing.
            if (!string.Equals(candidateVmId, vmId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
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
    /// One pass over the registry mirror of the cluster's resource table,
    /// yielding every VM resource's (resource ID, resource name, VM ID) -
    /// already trimmed of the brace formatting <see cref="FindResourceName"/>
    /// strips before comparing. Shared by <see cref="FindResourceName"/>,
    /// which filters this down to one caller-supplied VM ID, and
    /// <see cref="ListVmsAsync"/>, which wants every VM resource at once
    /// rather than a single match.
    /// </summary>
    private static IEnumerable<(string ResourceId, string ResourceName, string VmId)> EnumerateVmResources(
        CancellationToken cancellationToken)
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

            if (resource.GetValue("Name") as string is not { } resourceName || string.IsNullOrWhiteSpace(resourceName))
            {
                throw new InvalidOperationException(
                    $"cluster VM resource {resourceId} has VmID {candidate} but no readable Name value");
            }

            yield return (resourceId, resourceName, candidate.Trim('{', '}'));
        }
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

    /// <summary>
    /// Whether MSCluster_Node reports this host Up. Issue #14's Phase 3 is the
    /// first caller: the orphaned-checkpoint sweep has to skip a host that is
    /// still rebooting or draining rather than let a CIM call to it hang for
    /// its full <see cref="AgentOptions.HostOperationTimeout"/> budget on every
    /// pass, and the next interval pass picks the host back up once it settles
    /// - see <c>OrphanedCheckpointReaper</c>'s own remarks.
    /// </summary>
    public Task<bool> IsHostLiveAsync(string hostName, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var deadline = CimDeadline.After(_hostOperationTimeout);

            // Keyed on Name, MSCluster_Node's key property, so this does not
            // scan - the same reasoning ReadOwnerNode gives for
            // MSCluster_Resource. hostName is not caller-supplied in
            // practice (every value in existence comes back from this
            // service's own ListVmsAsync, as a ClusteredVm.OwningHost), but
            // escaped anyway rather than trusted to stay that way.
            var query = $"SELECT State FROM MSCluster_Node WHERE Name = '{WqlNames.EscapeLiteral(hostName)}'";

            using var session = CimSession.Create(null);
            var options = deadline.Options("reading MSCluster_Node.State", cancellationToken);
            foreach (var node in session.QueryInstances(NamespaceName, "WQL", query, options))
            {
                // ClusterNodeState: 0 Up, 1 Down, 2 Paused, 3 Joining. Only Up
                // counts as live - Paused and Joining both mean "do not route
                // new work here yet", which for a sweep is indistinguishable
                // from Down: either way this pass skips the host and the next
                // interval pass tries again once it settles.
                return node.CimInstanceProperties["State"]?.Value is { } state
                    && Convert.ToInt32(state) == 0;
            }

            // No such node in the cluster database - stale or renamed since
            // ListVmsAsync last reported it as an OwningHost. Not live is the
            // safe answer: skip it this pass rather than asking a host that
            // may not exist to enumerate anything.
            return false;
        }, cancellationToken);

    /// <summary>
    /// See <see cref="IClusterService.IsClusterMember"/>. The registry key
    /// every other method here already treats as proof of cluster
    /// membership - <see cref="EnumerateVmResources"/> throws when it is
    /// missing - checked directly instead, since this caller wants that as
    /// its own yes/no answer rather than as a side effect of an exception.
    /// </summary>
    public bool IsClusterMember()
    {
        using var resources = Registry.LocalMachine.OpenSubKey(ResourcesKeyPath);
        return resources is not null;
    }

    /// <summary>
    /// Every VM this cluster manages, with the host currently running it -
    /// see <see cref="IClusterService.ListVmsAsync"/> for why this exists and
    /// what it is expected to cost.
    /// </summary>
    /// <remarks>
    /// Two passes, not one per VM. The registry pass
    /// (<see cref="EnumerateVmResources"/>) does the resource-type filter -
    /// the entire point of this change, see <see cref="VirtualMachineResourceType"/> -
    /// entirely locally, at the ~0.04ms-per-read cost this class's own
    /// remarks measure. Owners then come from exactly one WMI query,
    /// unfiltered, joined against the registry's resource names in memory:
    /// resolving each VM's owner individually the way <see cref="ReadOwnerNode"/>
    /// does for <see cref="ResolveVmAsync"/>'s single VM would repeat the
    /// ~8-19ms-per-resource round trip those same remarks measure, once per
    /// VM in the cluster, which is exactly the O(VMs) cost this method exists
    /// to avoid.
    /// <para>
    /// The WMI query is not filtered with <c>WHERE Type = 'Virtual Machine'</c>,
    /// deliberately. The registry pass has already answered which resources
    /// are VMs; adding the same predicate to the WQL side would need this
    /// class to also assume WQL's <c>Type</c> comparison agrees with the
    /// registry's, a behaviour nothing here has measured. Reading every
    /// resource's owner and matching names in memory needs no such
    /// assumption - it only relies on <c>Name</c> being unique, which is
    /// <c>MSCluster_Resource</c>'s own key property.
    /// </para>
    /// </remarks>
    public Task<IReadOnlyList<ClusteredVm>> ListVmsAsync(CancellationToken cancellationToken) =>
        // Same synchronous-API trade every other method here makes: the
        // registry APIs and WMI both have no async surface, so this runs on
        // a pool thread.
        Task.Run<IReadOnlyList<ClusteredVm>>(() =>
        {
            var deadline = CimDeadline.After(_hostOperationTimeout);

            var vmResources = new List<(string ResourceName, string VmId)>();
            var seenVmIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (resourceId, resourceName, vmId) in EnumerateVmResources(cancellationToken))
            {
                // Same duplicate-VmID guard FindResourceName applies for a
                // single VM, generalized to the whole registry pass: two
                // resources sharing a VmID should be impossible, and an
                // ambiguous match should not be the exception that gets
                // guessed past.
                if (seenVmIds.TryGetValue(vmId, out var firstResourceName))
                {
                    throw new InvalidOperationException(
                        $"cluster VM resources {firstResourceName} and {resourceName} ({resourceId}) both have " +
                        $"VmID {vmId}, which should not be possible");
                }

                seenVmIds[vmId] = resourceName;
                vmResources.Add((resourceName, vmId));
            }

            if (vmResources.Count == 0)
            {
                return Array.Empty<ClusteredVm>();
            }

            // One unfiltered enumeration for every resource's owner, rather
            // than one WHERE-Name query per VM resource - see this method's
            // own remarks on the cost that would otherwise reintroduce.
            // OrdinalIgnoreCase, not Ordinal: these names arrive from the
            // registry mirror and from WMI, and FindResourceName's own
            // comment already records that those two sources do not agree on
            // case for the values they share. A case-sensitive join here
            // would drop VMs at random depending on how a resource happened
            // to be named.
            var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (var session = CimSession.Create(null))
            {
                var options = deadline.Options("reading MSCluster_Resource.OwnerNode", cancellationToken);
                foreach (var resource in session.QueryInstances(
                    NamespaceName, "WQL", "SELECT Name, OwnerNode FROM MSCluster_Resource", options))
                {
                    if (resource.CimInstanceProperties["Name"]?.Value is string name
                        && resource.CimInstanceProperties["OwnerNode"]?.Value is string owner)
                    {
                        owners[name] = owner;
                    }
                }
            }

            var vms = new List<ClusteredVm>(vmResources.Count);
            foreach (var (resourceName, vmId) in vmResources)
            {
                if (!owners.TryGetValue(resourceName, out var owner) || string.IsNullOrWhiteSpace(owner))
                {
                    // Two readings, the same pair ResolveVmAsync weighs for a
                    // single VM: either the cluster reports no owning node -
                    // not expected to be possible, since ownership transfers
                    // rather than lapsing - or the resource was deleted
                    // between the registry pass above and the query below,
                    // which is an entirely ordinary thing to catch a cluster
                    // doing.
                    //
                    // Skipped rather than thrown, which is the opposite of
                    // what ResolveVmAsync does with the identical fact, and
                    // the difference is what the caller does with the answer.
                    // That method answers about one VM for a detach that will
                    // act on it, where reporting "no such VM" lets a reclaim
                    // delete a disk a stopped VM still expects. This one
                    // answers "which VMs are worth looking at" for a sweep
                    // that acts on none of them directly - so the cost of
                    // omitting one is that its orphaned checkpoint, if it has
                    // one, waits for the next pass, while the cost of
                    // throwing is that every VM in the cluster waits, because
                    // this runs before the reaper's own per-host error
                    // handling can contain it.
                    //
                    // Loudly, though, not silently: an operator seeing this
                    // repeat for one VM is looking at a resource that is
                    // genuinely stuck rather than merely mid-delete.
                    _logger.LogWarning(
                        "the cluster reports no owning node for VM {VmId} (resource {ResourceName}); skipping it " +
                        "this pass, which leaves any checkpoint standing on it for the next one",
                        vmId, resourceName);
                    continue;
                }

                vms.Add(new ClusteredVm(vmId, owner));
            }

            return vms;
        }, cancellationToken);
}
