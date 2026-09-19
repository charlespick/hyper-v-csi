namespace HyperVCsiAgent.Core.HostControl;

/// <summary>
/// <see cref="IHyperVHostClient.FindDiskReferencesAsync"/>'s answer for one
/// host: the VMs whose storage references the path, and the VMs that could
/// not be told either way.
/// </summary>
/// <param name="VmIds">The VMs asked about whose storage references the path.</param>
/// <param name="Unresolved">
/// VMs none of whose disks was found to be the path, but at least one of
/// whose differencing chains could not be walked far enough to tell - only
/// ever reported with differencing chains included. A chain that cannot be
/// walked never hides another disk, on the same VM or another, that does
/// reference the path; it is reported here instead of read as "no".
/// </param>
public sealed record DiskReferences(IReadOnlyList<string> VmIds, IReadOnlyList<UnresolvedDiskReference> Unresolved);

/// <summary>A VM <see cref="DiskReferences"/> could not answer for, and why.</summary>
public sealed record UnresolvedDiskReference(string VmId, string Reason);
