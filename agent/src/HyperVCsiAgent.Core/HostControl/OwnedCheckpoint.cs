namespace HyperVCsiAgent.Core.HostControl;

/// <summary>
/// One checkpoint this driver tagged, found by a host-wide sweep rather than
/// asked for by (VM, snapshot) - see
/// <see cref="IHyperVHostClient.ListOwnedCheckpointsAsync"/> - paired with the
/// VM it stands on, since a sweep has no other way to say which VM's
/// configuration a given checkpoint came from.
/// </summary>
/// <param name="VmId">
/// The VM's own GUID - the same identifier every other member on this
/// interface takes as <c>vmId</c> - not the cluster resource name or any
/// other name a VM might also be known by.
/// </param>
public sealed record OwnedCheckpoint(string VmId, Checkpoint Checkpoint);
