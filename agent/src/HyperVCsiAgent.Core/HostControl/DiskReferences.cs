namespace HyperVCsiAgent.Core.HostControl;

/// <summary>
/// <see cref="IHyperVHostClient.FindDiskReferencesAsync"/>'s answer for one
/// host: the VMs whose storage references the path, and the VMs that could
/// not be told either way.
/// </summary>
/// <param name="VmIds">The VMs asked about whose storage references the path.</param>
/// <param name="Unresolved">
/// VMs none of whose disks was found to be the path, but at least one of
/// whose differencing chains could not be walked far enough to tell, or that
/// the walk ran out of time before reaching - only ever reported with
/// differencing chains included. A chain that cannot be
/// walked never hides another disk, on the same VM or another, that does
/// reference the path; it is reported here instead of read as "no".
/// </param>
/// <param name="RanOutOfTime">
/// The call's budget ran out before every VM's chains were walked, so the
/// host has not answered for all of its VMs - a host-wide gap, not one VM's,
/// which a caller weighing whether another VM also references the path has
/// to treat as a host it could not fully ask. <see cref="VmIds"/> still holds
/// whatever was found before then.
/// </param>
public sealed record DiskReferences(
    IReadOnlyList<string> VmIds, IReadOnlyList<UnresolvedDiskReference> Unresolved, bool RanOutOfTime = false);

/// <summary>A VM <see cref="DiskReferences"/> could not answer for, and why.</summary>
public sealed record UnresolvedDiskReference(string VmId, string Reason);
