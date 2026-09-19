# Finding which node has a CSV file open

The question this answers: given a path on a Cluster Shared Volume (a VHDX,
in this driver's case), which cluster node currently has it open — so that
node can be contacted directly, since the file cannot be opened locally to
ask it anything. This does not go through `vmms` or any Hyper-V API; it is
a pure NTFS/CSVFS question, answerable even for a file no VM currently
holds registered.

Everything below was exercised live against a real two-node cluster
(`csidev01`, `csidev02`), not derived from documentation alone — the
obvious approach from the docs turned out to have real gaps.

## The mechanism

CSVFS designates one node per CSV volume as the **coordinator** — the node
where NTFS/ReFS for that volume's disk is actually mounted
(`Get-ClusterSharedVolume`'s `OwnerNode`). Any node other than the
coordinator that opens a file on that volume does so through an internal,
SMB-based metadata channel back to the coordinator; that forwarding is
mandatory for metadata operations (open, close, rename, attribute/size
changes) regardless of which I/O redirection mode (Direct, Block
Redirected, File System Redirected) the volume's bulk data path is
currently in.

That internal channel is enumerable with the standard SMB cmdlets against
a special instance selector:

```powershell
Get-SmbOpenFile -SmbInstance CSV
```

This does surface remote opens, confirmed live. But every other detail
Microsoft's own reference example implies about the output shape was
wrong on this cluster (Windows Server 2025), and there is one blind spot
serious enough to invalidate a naive "empty result means nobody has it
open" reading.

## Calling this from C#: no PowerShell needed, but no plain WQL either

Consistent with [not shelling out to PowerShell](prefer-windows-apis-over-powershell.md)
anywhere else in this agent, `Get-SmbOpenFile -SmbInstance CSV` does not
need to be. But it isn't a plain CIM query either, and the difference
matters for how the C# call has to be built.

`Get-SmbOpenFile` is generated from a `.cdxml` (CIM cmdlets-over-objects)
definition —
`%windir%\System32\WindowsPowerShell\v1.0\Modules\SmbShare\SmbOpenFile.cdxml`
— which names the real class directly:
`ROOT/Microsoft/Windows/SMB/MSFT_SMBOpenFile`, provider `smbwmiv2`
(`SmbWmiV2.dll`). The `.cdxml` declares `SmbInstance` as a `QueryOption`
(`Default`=0, `CSV`=1, `SBL`=2, `SR`=3), not a `QueryableProperty` like
`FileId` or `ClientComputerName` — and that distinction is load-bearing:
`QueryableProperties` become ordinary WQL `WHERE` predicates, but
`QueryOptions` do not.

Proven live: a literal WQL query —
`SELECT * FROM MSFT_SmbOpenFile WHERE SmbInstance=1` — is rejected
outright by this provider with *"The query is not valid for the
specified query language."* It isn't that the value or syntax was wrong;
this dynamic provider does not implement WQL predicate filtering at all
for this property. A plain `Get-CimInstance -ClassName MSFT_SmbOpenFile`
with no filter also returns nothing, so there's no query-then-filter
sequence that works either — everything has to travel outside the query
text.

The actual channel is a **CIM custom operation option**, which
`Microsoft.Management.Infrastructure` (`CimSession`/`CimOperationOptions`)
exposes directly — this is the same library, not PowerShell, so it's a
normal C# call:

```csharp
using var session = CimSession.Create(coordinatorNodeName); // null for local
var options = new CimOperationOptions();
options.SetCustomOption("SmbInstance", (uint)1 /* CSV */, mustComply: false);

foreach (var instance in session.EnumerateInstances(
             "root/Microsoft/Windows/Smb", "MSFT_SmbOpenFile", options))
{
    var shareRelativePath = (string)instance.CimInstanceProperties["ShareRelativePath"].Value;
    var clientComputerName = (string)instance.CimInstanceProperties["ClientComputerName"].Value;
    // match shareRelativePath, then resolve clientComputerName per the NetFT section below
}
```

This was verified directly — bypassing `Get-SmbOpenFile` and the
`SmbShare` module entirely — both locally and against a genuinely remote
node (`CimSession.Create("csidev02")` called from `csidev01`), and it
reproduced the cmdlet's output exactly in both cases: the full CSV-open
listing when pointed at Volume1's coordinator, and the smaller,
Volume2-only listing when pointed at csidev02 (Volume2's own coordinator)
— exactly the coordinator-scoping behavior described below, not an
artifact of going through PowerShell.

There is still no server-side path filter available — `ShareRelativePath`
has to be matched client-side against every returned instance, the same
as the cmdlet does, because the provider has no WHERE-clause support for
any property, not just `SmbInstance`.

## What is actually true, measured live

**You must query the coordinator, not any node.** Querying
`-SmbInstance CSV` against the *non*-coordinator node for a given CSV
volume returns nothing for files on that volume, even while they are
genuinely open elsewhere. `Get-ClusterSharedVolume`'s `OwnerNode` has to
be resolved first, and the query pointed at that node specifically (locally
or via `-CimSession`).

**`ClientComputerName` is a link-local IPv6 address, not a hostname —
and that's consistent with what it actually is.** The [MSFT_SmbOpenFile
reference](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/smb/msft-smbopenfile)
defines it only as "the name of the computer from which the file handle
was opened," with no format guarantee. Empirically, for a CSV-instance
open it comes back as `[fe80::xxxx:xxxx:xxxx:xxxx]`: the client node's
address on the **Microsoft Failover Cluster Virtual Adapter** (NetFT) —
confirmed via `Get-NetAdapter -IncludeHidden` on the interface named
"Local Area Connection* 1" (`ComponentID: ROOT\NetFt`, `Hidden: True`).
NetFT is the cluster's private intra-node transport (heartbeat, CSV
redirected I/O, and this SMB metadata channel all ride on it), and it
self-assigns an APIPA-style `fe80::/64` address per node the way an
unconfigured NIC would — there is no DNS record and nothing to reverse
resolve. The doc's own example output shows a routable IPv4, which is
just a different (non-CSV) scenario where the client connected by a
routable address instead.

**`ClusterNodeName` is blank, and the reference explains why: it isn't
meant for this scenario.** Its documented purpose is "the computer that
is actually serving this handle... meaningful when the scope of the
session might be served by several servers in the cluster" — i.e.
Scale-Out File Server / continuously-available shares, where a client's
session can be transparently rebalanced across nodes serving the *same*
share. A CSV-instance open isn't that kind of session at all, so the
field being empty here looks like it's simply inapplicable rather than a
bug to work around.

**No SMB session backs a CSV-instance open, either.** The `SessionId` on
each row looks like it should cross-reference `Get-SmbSession`, but
querying that `SessionId` (and listing every session on the coordinator,
unfiltered) returned nothing. The internal CSV metadata channel doesn't
register as a session the normal SMB session cmdlets can see — it's a
separate, CSVFS-specific channel, not regular SMB3 file sharing that a
client happens to reach over a special instance.

**`Path` is a low-level device path — use `ShareRelativePath` instead.**
`Path` comes back as
`\\?\GLOBALROOT\Device\Harddisk1\ClusterPartition2\<vm>\Virtual Hard Disks\<file>.vhdx`,
useless for a direct comparison against a known `C:\ClusterStorage\...`
path. But `MSFT_SmbOpenFile` carries a second, undocumented-by-example
property that already solves this: `ShareRelativePath`, which came back
as `csidevnode01\Virtual Hard Disks\csidevnode01.vhdx` — exactly the CSV
path with the `C:\ClusterStorage\VolumeN\` prefix stripped. Match on that
directly rather than reconstructing or truncating `Path`.

**`-IncludeHidden` made no observable difference.** Both with and without
it, the same rows came back for a VHDX opened by a running VM. The
parameter's documented behavior (that hidden/internal opens are excluded
without it) did not reproduce here — pass it anyway since it costs
nothing, but do not rely on it being the reason an open is or isn't
visible.

## There is no built-in resolver for the NetFT address, by design

Before accepting "match it by hand" as final, every cluster-native surface
that could plausibly carry an IP-to-node mapping was checked directly
against this cluster, looking for something Windows already tracks that
would make the resolution step unnecessary:

- `Get-ClusterNetwork` lists exactly one network here, the routable
  `10.4.3.0/24` ("Cluster Network 1") — NetFT isn't modeled as a cluster
  network at all.
- `MSCluster_NetworkInterface` (WMI, `root\MSCluster`) only has entries
  for that same routable network's adapter on each node
  (`csidev01 - vEthernet (CSI Development Switch)`, etc.). No instance
  for NetFT exists.
- `MSCluster_NodeToNetworkInterface` — the association class that links a
  node to its interfaces — confirms the same thing from the other
  direction: each node associates only to its routable-network interface.
- `MSCluster_Node` has no address-shaped property at all (`Id`,
  `NodeInstanceID`, `UniqueID` are all present as properties but returned
  empty on this build).
- `Get-ClusterNetworkInterface` returns nothing.

This isn't a gap in the search — NetFT is deliberately excluded from the
cluster's public network model because it isn't a network an
administrator configures or a resource can depend on; it's self-managing
infrastructure. Its addresses are self-assigned the same way IPv4 APIPA
is, which is also why they aren't in DNS. **Matching against the
addresses each node reports on its own adapter isn't a workaround for a
missing feature — it's the only mechanism that exists**, because the
adapter that carries this traffic is intentionally invisible to every
higher-level cluster API.

The practical mitigation is caching, not avoidance: a NetFT address is
assigned once per node for as long as that node stays a cluster member
(comparable to how a NIC keeps its APIPA address until it's reconfigured
or the link drops) — it is not reassigned per file, per volume, or per
query. Build the address→node table with one fan-out across the cluster's
nodes (`SELECT Name, State FROM MSCluster_Node`, the read behind
`IClusterService.ListNodesAsync` — cluster-node-count-sized, so two calls
on this cluster) and cache it, refreshing on cluster membership change
rather than on every open-file lookup. That keeps the resolution step's
cost independent of how often files are queried.

### The call shape the agent uses

`Get-NetAdapter -IncludeHidden` is how the adapter was *identified*
above. It is not what the agent runs. `CimCsvNodeProbe.ReadNetFtAddressesAsync`
reads a node's addresses with one CIM query, through the same
`Microsoft.Management.Infrastructure` session every other remote read in
this agent uses rather than through PowerShell:

```csharp
using var session = CimSession.Create(nodeName); // null for local
var options = deadline.Options($"reading NetFT addresses on {nodeName}", cancellationToken);

foreach (var adapter in session.QueryInstances(
             @"root\cimv2", "WQL",
             "SELECT IPAddress FROM Win32_NetworkAdapterConfiguration WHERE ServiceName = 'NetFT'",
             options))
{
    // IPAddress is a string[] carrying both the IPv4 APIPA and the IPv6
    // link-local address the adapter self-assigns.
}
```

Keyed on `ServiceName` — the adapter's driver service — rather than on its
display name or interface alias. Both of those are localized and
renumbered, and "Local Area Connection* 1" above is exactly the shape
that cannot be matched on across an arbitrary cluster; the driver service
name is stable. Unlike the `MSFT_SmbOpenFile` read, this one is ordinary
WQL: `Win32_NetworkAdapterConfiguration` is a normal CIMv2 provider with
working `WHERE` support, so nothing has to travel as a custom operation
option here.

Measured at 60-90ms against a live node, which is why each node's read is
bounded at 10s rather than the full `HostOperationTimeout`: a node still
silent after that is not worth holding a table rebuild — and every lookup
waiting on it — for.

## A real hostname exists one layer down — not yet a usable join

Before settling for address-matching, it's worth being precise about
*why* `ClientComputerName` has no hostname: it isn't that SMB as a
protocol only knows an address. The authentication layer underneath this
channel does resolve a real computer name — `MSFT_SmbOpenFile` just
doesn't expose it. Confirmed by reading the Security event log's logon
events (4624) for this channel's authenticated identity:

```
Subject:                S-1-0-0 (anonymous)
New Logon:               CLIUSR
Logon Process:           Pku2uSsp, package NegoExtender
Workstation Name:        CSIDEV02
Source Network Address:  10.4.3.6
```

Two findings here, one closing a question and one opening a lead:

**The authenticated user (`CLIUSR`, `MSFT_SmbOpenFile.ClientUserName`)
genuinely cannot identify the node.** `Get-LocalUser -Name CLIUSR`
confirms it as a built-in account ("Failover Cluster Local Identity")
that Failover Clustering provisions with the *same name and SID* on
every node, specifically so cluster-internal traffic authenticates
without depending on domain trust being reachable. Every node presents
as an indistinguishable `CLIUSR`.

**But the logon event's `Workstation Name` field is a real, resolved
hostname (`CSIDEV02`), populated by the PKU2U/NegoExtender package this
cluster-peer handshake uses** — unlike NTLM's workstation-name field,
which is optional and often blank, this one came through populated on
every sample. This is a genuine hostname, sourced from the authentication
layer, not an address to look up at all.

**This is not yet load-bearing, because there is no proven join key from
a specific `MSFT_SmbOpenFile` row to a specific 4624 event.** The two are
keyed in different spaces — `FileId`/`SessionId` on one side, `LogonId`
on the other — and no field connecting them was found. What was observed
is consistent (every recent `CLIUSR` logon during this test showed
`CSIDEV02`, matching the one file independently known to be open from
there), but this cluster has only two nodes, so it cannot exercise
whether "most recent `CLIUSR` logon by timing" actually disambiguates
between three or more concurrently-active remote nodes rather than just
happening to agree in the only case with a single possible answer. Until
that's tested somewhere with more nodes, treat this as a lead, not a
replacement for the address-matching approach above.

One thing from the same event *is* solid, though: `Source Network
Address` here is `10.4.3.6` — the routable cluster network address, not
NetFT — and that address is already resolvable through
`MSCluster_NetworkInterface`, confirmed above as cluster-native and
present. If a reliable join from file to logon event is ever
established, resolving the result wouldn't need any hostname text
parsing at all; it would go through the same officially-modeled network
that NetFT is deliberately excluded from.

## The blind spot: the coordinator's own local opens are invisible

This is the gap the original research didn't find, and it matters more
than any of the cosmetic differences above.

A VM running locally *on the coordinator node itself* has its VHDX open
through a direct local NTFS handle — it never touches the SMB redirector,
because the redirector only exists to forward *remote* nodes' metadata
operations to the coordinator. Proven live: a VM running on `csidev01`
(also Volume1's coordinator) had a VHDX confirmed locked — a raw
`[System.IO.File]::Open(..., 'ReadWrite', 'None')` against it threw a
sharing violation — while `Get-SmbOpenFile -SmbInstance CSV` on that same
node, completely unfiltered, returned zero rows for it. Meanwhile, a
second VM's VHDX genuinely open from the *other* node showed up correctly
in the same unfiltered listing.

An empty result set is therefore ambiguous by construction: it means
either "nobody has this file open" or "the coordinator itself has it
open." It cannot mean anything else, though, because every non-local
opener is guaranteed to route through the channel this cmdlet reads —
which is what makes the ambiguity resolvable rather than fatal.

## Working algorithm

1. Confirm the file is actually open (e.g. the caller's own attempt to
   open it failed with a sharing violation — this driver already can't
   open the file locally, which is the premise for needing this at all).
2. Resolve the file's CSV volume and read its coordinator
   (`Get-ClusterSharedVolume` → `OwnerNode`).
3. Run `Get-SmbOpenFile -SmbInstance CSV -IncludeHidden` against that
   coordinator node, and match rows on `ShareRelativePath` against the
   CSV path with its `C:\ClusterStorage\VolumeN\` prefix stripped — not
   `Path`, which is a low-level device path.
4. A match: resolve `ClientComputerName` to a node using a cached
   address→node table (each node's own link-local address on its NetFT
   adapter, read per node with
   `SELECT IPAddress FROM Win32_NetworkAdapterConfiguration WHERE ServiceName = 'NetFT'`
   — see "The call shape the agent uses" above), rebuilt on cluster
   membership change rather than per lookup — see above for why no cluster
   API does this translation for you. That node is the holder.
5. No match: the holder is the coordinator node itself — the empty result
   is only unambiguous because step 1 already established the file is
   open by *someone*.

Step 5 depends on step 1 having been done first; skipping it turns a
correct "the coordinator holds it" conclusion into an incorrect "nobody
holds it" one.

## What this is not a substitute for

`FSCTL_IS_CSV_FILE` / `FSCTL_IS_FILE_ON_CSV_VOLUME` and
`FSCTL_IS_VOLUME_OWNED_BYCSVFS` answer "is this path on a CSVFS volume"
and "is this volume currently owned by CSVFS," not "who has this file
open" — they were not re-tested here since they don't bear on this
question at all.
