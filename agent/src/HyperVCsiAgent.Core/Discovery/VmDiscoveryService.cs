using HyperVCsiAgent.Core.Cluster;
using Microsoft.Extensions.Logging;

namespace HyperVCsiAgent.Core.Discovery;

/// <summary>
/// Finds the VM a Kubernetes node is running in from the node's SMBIOS UUID
/// (<c>status.nodeInfo.systemUUID</c>, which is the VM's <c>BIOSGUID</c>), so
/// the controller can record the VM ID on the Node object once and every later
/// request can carry the VM ID the Hyper-V and cluster APIs key on natively.
/// </summary>
/// <remarks>
/// A fan-out, which everything else in the agent avoids, and acceptable here
/// only because of when it runs: once per Kubernetes node, when the controller
/// first sees it (or sees its <c>systemUUID</c> change), never on a request
/// path. Nothing cluster-wide indexes <c>BIOSGUID</c> - see
/// <see cref="IVmBiosGuidLookup"/> - so every node that is up is asked, all at
/// once, which bounds the cost at one host's round trip rather than the sum of
/// up to 64.
/// <para>
/// Complete at the first match: the remaining hosts are not waited for and no
/// duplicate is looked for. A node VM copied from another without being given a
/// new <c>BIOSGUID</c> shares the original's - Hyper-V copies keep it - and
/// would be resolved to whichever VM answered first. That risk is accepted and
/// documented for operators rather than designed around.
/// </para>
/// <para>
/// Not finding a VM is only an answer when every host answered. If any host
/// that is up failed to, the VM may be on exactly that host, so the result is
/// <see cref="VmDiscoveryIncompleteException"/> - retryable - rather than null.
/// </para>
/// </remarks>
public sealed class VmDiscoveryService(
    IClusterService cluster,
    IVmBiosGuidLookup lookup,
    ILogger<VmDiscoveryService> logger)
{
    /// <summary>
    /// The VM whose <c>BIOSGUID</c> is <paramref name="biosGuid"/>, or null when
    /// every node that is up answered and none of them has it.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="biosGuid"/> is not an 8-4-4-4-12 GUID.</exception>
    /// <exception cref="VmDiscoveryIncompleteException">
    /// No match, and the cluster's node list could not be read or at least one
    /// node did not answer.
    /// </exception>
    public async Task<DiscoveredVm?> DiscoverAsync(string biosGuid, CancellationToken cancellationToken)
    {
        // "D" is the bare 8-4-4-4-12 form, and the one a WQL filter on BIOSGUID
        // needs: vmms stores the value without braces, and a braced literal
        // silently matches nothing. Lowercased, which the filter does not care
        // about (measured), so the answer echoes one spelling whatever the
        // caller sent.
        if (!Guid.TryParseExact(biosGuid, "D", out var parsed))
        {
            throw new ArgumentException($"{biosGuid} is not a GUID in 8-4-4-4-12 form", nameof(biosGuid));
        }

        var canonical = parsed.ToString("D");

        IReadOnlyList<string> nodes;
        try
        {
            nodes = await cluster.ListNodesAsync(cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new VmDiscoveryIncompleteException(
                $"could not list the cluster's nodes to look for BIOSGUID {canonical}: {ex.Message}", [], ex);
        }

        if (nodes.Count == 0)
        {
            // ListNodesAsync leaves out only Down nodes, and the agent is itself
            // running on a node, so this is a cluster mid-upheaval rather than
            // one that has looked everywhere.
            throw new VmDiscoveryIncompleteException(
                $"the cluster reports no node that is up, so there is nowhere to look for BIOSGUID {canonical}", []);
        }

        logger.LogDebug("looking for BIOSGUID {BiosGuid} on {Nodes}", canonical, nodes);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pending = new Dictionary<Task<string?>, string>();
        foreach (var node in nodes)
        {
            // Task.Run so a lookup that throws synchronously is one host's
            // failure, recorded below, rather than an exception out of this
            // loop that abandons every host already asked.
            pending.Add(Task.Run(() => lookup.FindVmIdByBiosGuidAsync(node, canonical, stop.Token), stop.Token), node);
        }

        var unanswered = new List<string>();
        Exception? firstFailure = null;
        while (pending.Count > 0)
        {
            var finished = await Task.WhenAny(pending.Keys);
            var host = pending[finished];
            pending.Remove(finished);

            string? vmId;
            try
            {
                vmId = await finished;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "{HostName} did not answer whether it runs the VM with BIOSGUID {BiosGuid}", host, canonical);
                unanswered.Add(host);
                firstFailure ??= ex;
                continue;
            }

            if (vmId is null)
            {
                continue;
            }

            // First match wins. The hosts still being asked are told to stop,
            // and left to finish on their own - a CIM call already inside its
            // RPC ignores the token and runs out its deadline, which there is
            // no reason to make the caller wait for. Their outcomes are
            // observed so a late failure is not reported as unobserved.
            stop.Cancel();
            foreach (var straggler in pending.Keys)
            {
                _ = straggler.ContinueWith(
                    static t => _ = t.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }

            logger.LogInformation("BIOSGUID {BiosGuid} is VM {VmId}, found on {HostName}", canonical, vmId, host);
            return new DiscoveredVm(canonical, vmId, host);
        }

        if (unanswered.Count > 0)
        {
            throw new VmDiscoveryIncompleteException(
                $"no node that answered runs a VM with BIOSGUID {canonical}, but {string.Join(", ", unanswered)} " +
                $"did not answer, and the VM may be there: {firstFailure?.Message}",
                unanswered, firstFailure);
        }

        logger.LogInformation("no node runs a VM with BIOSGUID {BiosGuid}; asked {Nodes}", canonical, nodes);
        return null;
    }
}
