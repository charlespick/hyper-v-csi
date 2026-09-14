using System.Net;
using System.Net.Sockets;
using HyperVCsiAgent.Core.Cluster;

namespace HyperVCsiAgent.Service.HostControl;

/// <summary>
/// Maps the address a CSV open-file listing names its client by - a node's
/// address on the Failover Cluster Virtual Adapter (NetFT) - back to that
/// node's name.
/// </summary>
/// <remarks>
/// No cluster API makes this translation: NetFT is deliberately absent from
/// every network model the cluster exposes (docs/csv-file-open-ownership.md
/// lists each one checked), so the table is built by asking every node for its
/// own adapter's addresses. That makes this the one part of locating a VHDX
/// whose cost grows with the number of nodes rather than being a keyed read,
/// which is why it is cached for as long as it is.
/// <para>
/// A node keeps its NetFT addresses for as long as it stays a cluster member,
/// so nothing here refreshes per lookup. The table is rebuilt on two triggers:
/// an address it has no entry for - the first thing a node that joined since
/// the last build looks like from here - and a long periodic revalidation, in
/// case a change is ever missed that way.
/// </para>
/// <para>
/// Only the first build and a miss make a lookup wait. Revalidation happens in
/// the background while lookups keep answering from the table already built,
/// and every rebuild leaves out nodes the cluster reports Down
/// (<see cref="IClusterService.ListNodesAsync"/>) - so a node that has just
/// died, the very moment an agent failing over onto a survivor first needs
/// this table, costs no lookup a CIM timeout.
/// </para>
/// <para>
/// Each rebuild asks every node independently. A node that does not answer
/// keeps the entries it already had rather than costing every other node
/// theirs: its addresses only change if its adapter was reconfigured, and the
/// next rebuild it does answer puts that right.
/// </para>
/// </remarks>
public sealed class NetFtAddressTable
{
    /// <summary>
    /// How long a built table is used before the next lookup starts a
    /// background rebuild. Minutes, not the seconds the coordinator reading
    /// gets: this is the fan-out, and what it caches does not move while
    /// membership holds.
    /// </summary>
    public static readonly TimeSpan RevalidationInterval = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The least time between two rebuilds triggered by a miss. An address that
    /// genuinely belongs to no node would otherwise fan out to every node on
    /// every lookup that meets it.
    /// </summary>
    public static readonly TimeSpan MissRebuildSpacing = TimeSpan.FromSeconds(30);

    private readonly IClusterService _cluster;
    private readonly ICsvNodeProbe _probe;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<NetFtAddressTable> _logger;
    private readonly SemaphoreSlim _rebuildLock = new(1, 1);

    /// <summary>
    /// Replaced wholesale, never mutated, so a lookup reads it without the lock.
    /// </summary>
    private volatile Table _table = Table.Empty;

    /// <summary>1 while a background revalidation is under way, so lookups start at most one.</summary>
    private int _revalidating;

    public NetFtAddressTable(
        IClusterService cluster, ICsvNodeProbe probe, TimeProvider timeProvider, ILogger<NetFtAddressTable> logger)
    {
        _cluster = cluster;
        _probe = probe;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// The node <paramref name="clientComputerName"/> belongs to, or null when
    /// no node in the cluster carries it even after a rebuild.
    /// </summary>
    /// <remarks>
    /// Takes the listing's own spelling - a bracketed IPv6 literal - as readily
    /// as a bare address with or without a zone index. A client named by a
    /// node name rather than an address is answered as that node, if it is one.
    /// </remarks>
    public async Task<string?> ResolveAsync(string clientComputerName, CancellationToken cancellationToken)
    {
        var key = Normalize(clientComputerName);
        var table = _table;

        if (table.BuiltAt is not { } builtAt)
        {
            table = await RebuildAsync(table, missed: false, cancellationToken).ConfigureAwait(false);
        }
        else if (_timeProvider.GetUtcNow() - builtAt >= RevalidationInterval)
        {
            // Due, not wrong: every entry was true when it was read and stays
            // true while its node stays a member. Answered from it now, and
            // rebuilt behind this lookup rather than in front of it.
            StartRevalidation(table);
        }

        if (table.Find(key) is { } node)
        {
            return node;
        }

        table = await RebuildAsync(table, missed: true, cancellationToken).ConfigureAwait(false);
        return table.Find(key);
    }

    /// <summary>
    /// The one spelling an address is keyed by: brackets and zone index
    /// dropped, IPv4-mapped IPv6 unmapped. Anything that is not an address
    /// comes back trimmed and otherwise as it was.
    /// </summary>
    public static string Normalize(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
        {
            trimmed = trimmed[1..^1];
        }

        if (!IPAddress.TryParse(trimmed, out var address))
        {
            return trimmed;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        // A link-local address is only unique together with its zone, but the
        // zone is the local interface index - different on every node for the
        // same adapter, and absent altogether from the listing's spelling.
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            address.ScopeId = 0;
        }

        return address.ToString();
    }

    private void StartRevalidation(Table seen)
    {
        if (Interlocked.CompareExchange(ref _revalidating, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                // No caller's token: no caller is waiting on this, and the
                // per-node reads are bounded by their own CIM deadlines.
                await RebuildAsync(seen, missed: false, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "revalidating the NetFT address table failed; keeping the entries it has");
            }
            finally
            {
                Interlocked.Exchange(ref _revalidating, 0);
            }
        });
    }

    private async Task<Table> RebuildAsync(Table seen, bool missed, CancellationToken cancellationToken)
    {
        await _rebuildLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = _table;

            // Someone else rebuilt while this caller waited for the lock, and
            // theirs is as fresh as this one would have been.
            if (!ReferenceEquals(current, seen))
            {
                return current;
            }

            if (missed && current.BuiltAt is { } builtAt && _timeProvider.GetUtcNow() - builtAt < MissRebuildSpacing)
            {
                return current;
            }

            IReadOnlyList<string> nodes;
            try
            {
                nodes = await _cluster.ListNodesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && current.BuiltAt is not null)
            {
                // With nothing built yet there is nothing to fall back on, and
                // the filter lets that case throw. Otherwise the old table is
                // still the best answer there is, and re-dated so this does not
                // retry on every lookup until the cluster answers again.
                _logger.LogWarning(ex,
                    "could not list cluster nodes to rebuild the NetFT address table; keeping the entries it has");
                _table = current with { BuiltAt = _timeProvider.GetUtcNow() };
                return _table;
            }

            var reads = await Task.WhenAll(nodes.Select(node => ReadNodeAsync(node, cancellationToken))).ConfigureAwait(false);

            var addressesByNode = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (node, addresses) in reads)
            {
                if (addresses is not null)
                {
                    addressesByNode[node] = addresses;
                }
                else if (current.AddressesByNode.TryGetValue(node, out var previous))
                {
                    addressesByNode[node] = previous;
                }
            }

            _table = Table.Build(nodes, addressesByNode, _timeProvider.GetUtcNow(), _logger);
            _logger.LogDebug("rebuilt the NetFT address table for {Count} nodes", nodes.Count);
            return _table;
        }
        finally
        {
            _rebuildLock.Release();
        }
    }

    private async Task<(string Node, IReadOnlyList<string>? Addresses)> ReadNodeAsync(
        string node, CancellationToken cancellationToken)
    {
        try
        {
            var addresses = await _probe.ReadNetFtAddressesAsync(node, cancellationToken).ConfigureAwait(false);
            return (node, addresses.Select(Normalize).ToArray());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "could not read {Node}'s NetFT addresses; keeping whatever the table already had for it", node);
            return (node, null);
        }
    }

    private sealed record Table(
        IReadOnlyDictionary<string, IReadOnlyList<string>> AddressesByNode,
        IReadOnlyDictionary<string, string> NodeByAddress,
        IReadOnlyDictionary<string, string> NodeNames,
        DateTimeOffset? BuiltAt)
    {
        public static readonly Table Empty = new(
            new Dictionary<string, IReadOnlyList<string>>(),
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            null);

        public string? Find(string key) =>
            NodeByAddress.TryGetValue(key, out var node) ? node
            : NodeNames.TryGetValue(key, out var name) ? name
            : null;

        public static Table Build(
            IReadOnlyList<string> nodes,
            IReadOnlyDictionary<string, IReadOnlyList<string>> addressesByNode,
            DateTimeOffset builtAt,
            ILogger logger)
        {
            var nodeByAddress = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var contested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (node, addresses) in addressesByNode)
            {
                foreach (var address in addresses)
                {
                    if (contested.Contains(address))
                    {
                        continue;
                    }

                    if (nodeByAddress.TryGetValue(address, out var other) && !string.Equals(other, node, StringComparison.OrdinalIgnoreCase))
                    {
                        // Not expected - NetFT assigns these to be unique - but an
                        // address two nodes both claim identifies neither, so it
                        // resolves to nothing rather than to whichever came first.
                        logger.LogWarning(
                            "NetFT address {Address} is reported by both {Node} and {OtherNode}; it will not resolve to either",
                            address, node, other);
                        nodeByAddress.Remove(address);
                        contested.Add(address);
                        continue;
                    }

                    nodeByAddress[address] = node;
                }
            }

            var nodeNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in nodes)
            {
                nodeNames[node] = node;
            }

            return new Table(addressesByNode, nodeByAddress, nodeNames, builtAt);
        }
    }
}
