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

## What is actually true, measured live

**You must query the coordinator, not any node.** Querying
`-SmbInstance CSV` against the *non*-coordinator node for a given CSV
volume returns nothing for files on that volume, even while they are
genuinely open elsewhere. `Get-ClusterSharedVolume`'s `OwnerNode` has to
be resolved first, and the query pointed at that node specifically (locally
or via `-CimSession`).

**`ClientComputerName` is a link-local IPv6 address, not a hostname.** It
comes back as `[fe80::xxxx:xxxx:xxxx:xxxx]` — the address of the client
node's private cluster-communication NIC (the same adapter carrying
cluster heartbeat, named "Local Area Connection* 1" in this environment;
distinct from the CSV volume's own virtual switch). It is link-local, so
it is not in DNS and cannot be resolved by name lookup. The only way to
turn it into a node name is to collect every cluster node's own link-local
addresses on that adapter (`Get-NetIPAddress -AddressFamily IPv6`) once,
and match the string. Microsoft's documented example output shows a
routable IP and a resolved `ClusterNodeName` for this field; neither
matches what this cluster actually returns.

**`ClusterNodeName` is blank.** Every row returned it empty. It cannot be
used as a shortcut around the address-matching step above, at least on
this OS build.

**`Path` is a low-level device path, not the friendly CSV path.** Rows
come back as
`\\?\GLOBALROOT\Device\Harddisk1\ClusterPartition2\<vm>\Virtual Hard Disks\<file>.vhdx`,
not `C:\ClusterStorage\Volume1\<vm>\Virtual Hard Disks\<file>.vhdx`. A
direct string comparison against a known `C:\ClusterStorage\...` path will
never match; matching has to be done on the trailing path (parent
directory name plus filename) instead.

**`-IncludeHidden` made no observable difference.** Both with and without
it, the same rows came back for a VHDX opened by a running VM. The
parameter's documented behavior (that hidden/internal opens are excluded
without it) did not reproduce here — pass it anyway since it costs
nothing, but do not rely on it being the reason an open is or isn't
visible.

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
   coordinator node, and match rows by trailing path (parent directory +
   filename), not by full path string.
4. A match: resolve `ClientComputerName` to a node by comparing it against
   every cluster node's own link-local address on the private
   cluster-communication adapter. That node is the holder.
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
