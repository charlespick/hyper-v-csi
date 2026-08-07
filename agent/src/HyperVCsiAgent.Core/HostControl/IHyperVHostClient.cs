namespace HyperVCsiAgent.Core.HostControl;

/// <summary>
/// CIM (root\virtualization\v2) calls targeted at the host resolved by
/// IClusterService as the current VM owner. The agent is the only thing ever
/// permitted to initiate this - no Kubernetes component, and no other host, ever does.
/// </summary>
/// <remarks>
/// Every method takes the host explicitly rather than holding a session, because
/// the owning host is resolved per operation and can change between two calls
/// for the same VM.
/// </remarks>
public interface IHyperVHostClient
{
    /// <summary>
    /// Finds the VHDX in the VM's configuration, or null when it isn't attached.
    /// This is configuration data, so it answers whether or not the VM is
    /// running - the property a file lock notably lacks.
    /// </summary>
    /// <exception cref="VmNotOnHostException">The VM is not registered on this host.</exception>
    Task<AttachedDisk?> FindAttachedDiskAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken);

    /// <summary>
    /// Whether the VHDX is in the VM's configuration at all.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="FindAttachedDiskAsync"/> because detach must not
    /// depend on anything it does not need. That one reports the disk's address
    /// and fails when the address cannot be read, which is right for attach - a
    /// wrong address sends the node plugin to the wrong disk. Detach never uses
    /// the address, and failing on it would leave the disk attached forever
    /// behind a retry that cannot succeed, with the VolumeAttachment, the PV's
    /// deletion, and the node's drain all stuck behind it.
    /// </remarks>
    /// <exception cref="VmNotOnHostException">The VM is not registered on this host.</exception>
    Task<bool> IsDiskAttachedAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken);

    /// <summary>
    /// Finds an unoccupied address on one of the VM's existing SCSI controllers,
    /// or null when every one of them is full.
    /// </summary>
    /// <exception cref="VmNotOnHostException">The VM is not registered on this host.</exception>
    Task<DiskSlot?> FindFreeSlotAsync(string hostName, string vmId, CancellationToken cancellationToken);

    /// <summary>
    /// Attaches the VHDX at the given slot.
    /// </summary>
    /// <exception cref="VmNotOnHostException">The VM is not registered on this host.</exception>
    Task AttachDiskAsync(string hostName, string vmId, string vhdxPath, DiskSlot slot, CancellationToken cancellationToken);

    Task DetachDiskAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken);

    /// <summary>
    /// Reads a VHDX's current virtual size through the host running the VM
    /// it's attached to, rather than opening the file locally - the local open
    /// is exactly what the running VM's own exclusive hold on the file defeats,
    /// per <see cref="Storage.VhdxInUseException"/>. <paramref name="vmId"/>
    /// names nothing the underlying call needs - it operates on the path alone
    /// - and is carried through only so a failure can be logged against the VM
    /// it concerns, the same reason every other method on this interface takes
    /// it.
    /// </summary>
    Task<long> GetDiskSizeAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken);

    /// <summary>
    /// Grows a VHDX through the host running the VM it's attached to, and
    /// returns the size it actually ended up at. Msvm_ImageManagementService
    /// supports this against an attached, running disk - that capability is the
    /// entire basis for CSI's ONLINE expansion claim - but only when asked from
    /// the host actually running the VM; the same call issued from a peer host
    /// collides with that VM's exclusive hold on the file, exactly as
    /// <see cref="GetDiskSizeAsync"/> does.
    /// </summary>
    Task<long> ResizeDiskAsync(string hostName, string vmId, string vhdxPath, long newSizeBytes, CancellationToken cancellationToken);

    /// <summary>
    /// Classifies how <paramref name="vhdxPath"/> relates to the VM's current
    /// configuration - whether it is not referenced at all, is the VM's live
    /// disk directly, or sits behind a differencing chain, and if so whether
    /// that chain is rooted in a checkpoint this driver tagged (found by
    /// <paramref name="ownedCheckpointElementNamePrefix"/>) or a foreign one.
    /// </summary>
    /// <remarks>
    /// Deliberately separate from <see cref="IsDiskAttachedAsync"/> rather than
    /// a change to it: that method's callers - attach, detach, expand - are
    /// correct to treat any unresolved chain as a hard failure requiring an
    /// operator, and must keep doing so. Only a snapshot of an attached volume
    /// has a legitimate reason to find its own prior, incomplete attempt sitting
    /// on the VM and resume past it instead.
    /// </remarks>
    /// <exception cref="VmNotOnHostException">The VM is not registered on this host.</exception>
    /// <exception cref="InvalidOperationException">
    /// The VHDX sits behind a differencing chain that is not rooted in a
    /// checkpoint carrying <paramref name="ownedCheckpointElementNamePrefix"/> -
    /// a foreign checkpoint, a backup product's recovery point, or a chain this
    /// method could not walk to a conclusion within its depth bound. Refused
    /// rather than guessed past, on <c>GuardAgainstDifferencingChain</c>'s own
    /// reasoning.
    /// </exception>
    Task<VolumeAttachment> ClassifyAttachmentAsync(
        string hostName, string vmId, string vhdxPath, string ownedCheckpointElementNamePrefix, CancellationToken cancellationToken);

    /// <summary>
    /// Takes a disk-only checkpoint of the whole VM - Hyper-V has no notion of
    /// checkpointing a single disk - and tags it with this driver's identity so
    /// a later call can recognize it as its own.
    /// </summary>
    /// <remarks>
    /// "Disk-only" here means a Production checkpoint (<c>SnapshotType</c> 2,
    /// which despite its "Full Snapshot" label is what a VSS-quiesced,
    /// no-saved-state checkpoint actually goes through), not the differencing
    /// disk snapshot type Hyper-V's schema documents at value 3 - measured
    /// against a real host, that value is rejected outright regardless of the
    /// VM's own checkpoint setting. See CreateSnapshot's ValueMap for the
    /// documented-but-unimplemented "Disk Snapshot" this is not using.
    /// <para>
    /// Consistency is requested as Crash Consistent, not Application
    /// Consistent: the guest's VSS integration has no visibility into whatever
    /// is actually running as containers on the node, so asking for more than
    /// crash consistency would not deliver it - see design.md's snapshot notes.
    /// </para>
    /// <para>
    /// Tagging is a second CIM call after creation, not part of it:
    /// <c>CreateSnapshot</c>'s <c>SnapshotSettings</c> input does not apply
    /// <c>ElementName</c> - measured, despite nothing in the schema saying so -
    /// so this method finds the checkpoint it just created and renames it via
    /// <c>ModifySystemSettings</c> before returning.
    /// </para>
    /// </remarks>
    /// <param name="elementName">
    /// This checkpoint's identity, conventionally
    /// <c>hyperv-csi/&lt;driverInstance&gt;/&lt;volumeId&gt;/&lt;snapshotName&gt;</c> -
    /// what <see cref="FindOwnedCheckpointAsync"/> and
    /// <see cref="ClassifyAttachmentAsync"/> match against later.
    /// </param>
    /// <param name="notesJson">Freeform detail carried on the checkpoint alongside its identity, opaque to this seam.</param>
    /// <exception cref="CheckpointsNotConfiguredException">
    /// The VM is not set to <c>ProductionOnly</c> checkpoints. Checked before
    /// anything else: creating a checkpoint that could silently fall back to a
    /// Standard one is exactly the outcome this whole capability exists to rule
    /// out.
    /// </exception>
    /// <exception cref="VmNotOnHostException">The VM is not registered on this host.</exception>
    Task<Checkpoint> CreateCheckpointAsync(
        string hostName, string vmId, string elementName, string notesJson, CancellationToken cancellationToken);

    /// <summary>
    /// Finds this driver's own checkpoint on the VM, by <c>ElementName</c>
    /// prefix, or null if there is none. The only way anything here learns
    /// about a checkpoint a previous attempt left behind - nothing persists a
    /// <see cref="Checkpoint"/> across a call boundary, so recovery re-derives
    /// it exactly the way every other piece of state in this design does.
    /// </summary>
    /// <exception cref="VmNotOnHostException">The VM is not registered on this host.</exception>
    /// <exception cref="InvalidOperationException">
    /// More than one checkpoint carries this prefix, which should be impossible
    /// under the per-VM job serialization every caller relies on and is refused
    /// rather than guessed past.
    /// </exception>
    Task<Checkpoint?> FindOwnedCheckpointAsync(
        string hostName, string vmId, string elementNamePrefix, CancellationToken cancellationToken);

    /// <summary>
    /// Starts merging the checkpoint back into its base and returns once the
    /// merge has started, without waiting for it to finish.
    /// </summary>
    /// <remarks>
    /// Deliberately fire-and-forget. vmms owns finishing a live merge once
    /// <c>DestroySnapshot</c> has been issued, independent of this process -
    /// including surviving the agent exiting or restarting mid-merge - which is
    /// what makes this the safest step in the whole operation to be interrupted
    /// during. How long the merge itself takes depends on how much was written
    /// through the checkpoint while it stood, which for a multi-hour copy behind
    /// it can be a great deal more than the sub-second case an idle checkpoint
    /// measures at.
    /// </remarks>
    /// <exception cref="VmNotOnHostException">The VM is not registered on this host.</exception>
    Task DestroyCheckpointAsync(string hostName, Checkpoint checkpoint, CancellationToken cancellationToken);
}
