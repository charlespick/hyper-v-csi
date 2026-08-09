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
    /// disk directly, or sits behind a differencing chain, and if so which of
    /// three things is true about the checkpoint at that chain's root: it is
    /// this exact snapshot's own (an exact match against
    /// <paramref name="thisSnapshotElementName"/> - see
    /// <see cref="CheckpointMatching.FindExact"/>), it is this driver's but
    /// some *other* snapshot's (<see cref="CheckpointMatching.FindAnyOwned"/>,
    /// tried only once the exact match has already failed), or it is
    /// genuinely foreign.
    /// </summary>
    /// <remarks>
    /// Deliberately separate from <see cref="IsDiskAttachedAsync"/> rather than
    /// a change to it: that method's callers - attach, detach, expand - are
    /// correct to treat any unresolved chain as a hard failure requiring an
    /// operator, and must keep doing so. Only a snapshot of an attached volume
    /// has a legitimate reason to find its own prior, incomplete attempt sitting
    /// on the VM and resume past it instead - or to find a *sibling* volume's
    /// checkpoint in the way and wait rather than fail hard, since a checkpoint
    /// is VM-wide and taking one for any one volume re-points every other disk
    /// on the same VM too.
    /// </remarks>
    /// <param name="thisSnapshotElementName">
    /// The fully-qualified identity of the checkpoint *this* snapshot attempt
    /// would take - matched exactly, never as a prefix. Prefix matching this
    /// against every checkpoint's <c>ElementName</c> is what previously let a
    /// snapshot named e.g. <c>snap</c> adopt, and later destroy, a checkpoint
    /// actually standing for a different snapshot named <c>snap-2</c>.
    /// </param>
    /// <exception cref="VmNotOnHostException">The VM is not registered on this host.</exception>
    /// <exception cref="InvalidOperationException">
    /// The VHDX sits behind a differencing chain that is not rooted in any
    /// checkpoint this driver tagged at all - a foreign checkpoint, a backup
    /// product's recovery point, or a chain this method could not walk to a
    /// conclusion within its depth bound. Refused rather than guessed past, on
    /// <c>GuardAgainstDifferencingChain</c>'s own reasoning. A chain rooted in
    /// a checkpoint this driver tagged for a *different* snapshot does not
    /// throw this - see <see cref="VolumeAttachment"/>'s
    /// <c>BehindOtherSnapshotsCheckpoint</c>.
    /// </exception>
    Task<VolumeAttachment> ClassifyAttachmentAsync(
        string hostName, string vmId, string vhdxPath, string thisSnapshotElementName, CancellationToken cancellationToken);

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
    /// <c>hyperv-csi/&lt;volumeId&gt;/&lt;snapshotName&gt;</c> - see
    /// <see cref="CheckpointMatching"/> for the driver-level prefix half of
    /// that convention, and <see cref="FindOwnedCheckpointAsync"/> and
    /// <see cref="ClassifyAttachmentAsync"/> for how each half is matched
    /// again later. Supporting more than one deployment of this driver on one
    /// cluster - which would need a segment identifying which deployment
    /// took a given checkpoint - is not in scope: this convention names
    /// nothing beyond the volume and the snapshot.
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
    /// Finds this driver's own checkpoint for *this exact* (volume, snapshot)
    /// pair - an exact match on <paramref name="elementName"/>, see
    /// <see cref="CheckpointMatching.FindExact"/>, never a prefix - or null if
    /// there is none. The only way anything here learns about a checkpoint a
    /// previous attempt left behind - nothing persists a <see cref="Checkpoint"/>
    /// across a call boundary, so recovery re-derives it exactly the way every
    /// other piece of state in this design does.
    /// </summary>
    /// <remarks>
    /// Deliberately exact-only rather than also exposing
    /// <see cref="CheckpointMatching.FindAnyOwned"/>'s driver-wide prefix
    /// match: every caller of this method - <c>SnapshotService</c>'s
    /// re-check under its per-VM checkpoint lock - already has the exact
    /// per-snapshot name in hand, and the one caller that genuinely needs the
    /// "is anything of ours standing at all" question,
    /// <c>CimHyperVHostClient.ClassifyAttachment</c>, asks it internally
    /// rather than through the interface. Keeping that second mode out of the
    /// public seam entirely is what keeps it narrow.
    /// </remarks>
    /// <exception cref="VmNotOnHostException">The VM is not registered on this host.</exception>
    /// <exception cref="InvalidOperationException">
    /// More than one checkpoint carries this exact name, which should be
    /// impossible under the per-VM job serialization every caller relies on
    /// combined with <paramref name="elementName"/> being unique per (volume,
    /// snapshot) pair, and is refused rather than guessed past.
    /// </exception>
    Task<Checkpoint?> FindOwnedCheckpointAsync(
        string hostName, string vmId, string elementName, CancellationToken cancellationToken);

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

    /// <summary>
    /// Every checkpoint on this host whose <c>ElementName</c> starts with
    /// <see cref="CheckpointMatching.OwnedPrefix"/>, paired with the VM each
    /// one stands on.
    /// </summary>
    /// <remarks>
    /// Host-scoped rather than VM-scoped, deliberately: the caller is a sweep
    /// looking for checkpoints nothing is driving anymore, and it does not
    /// know which VMs to ask about - that is exactly the question it is
    /// asking. Every other checkpoint member on this interface takes a
    /// <c>vmId</c> because its caller already has one in hand; this is the one
    /// place that does not, and cannot.
    /// <para>
    /// This is also the one place <see cref="CheckpointMatching.FindAnyOwned"/>'s
    /// driver-level question - "is this checkpoint ours at all, regardless of
    /// which (volume, snapshot) it names" - is exposed through this interface.
    /// <see cref="FindOwnedCheckpointAsync"/>'s own remarks deliberately keep
    /// that question internal to <c>CimHyperVHostClient</c> rather than
    /// letting it out through the public seam, because every caller of that
    /// method already has a specific snapshot in mind and the exact match is
    /// all it needs. A sweep has no specific snapshot in mind by construction
    /// - that is the whole reason it is sweeping - so the driver-level
    /// question is the only one it can ask, and narrowing this method to an
    /// exact match the way <see cref="FindOwnedCheckpointAsync"/> does would
    /// leave it with no way to ask anything at all.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<OwnedCheckpoint>> ListOwnedCheckpointsAsync(string hostName, CancellationToken cancellationToken);

    /// <summary>
    /// Whether the VM is configured for <c>ProductionOnly</c> checkpoints -
    /// the one precondition <see cref="CreateCheckpointAsync"/> requires and
    /// refuses to proceed without.
    /// </summary>
    /// <remarks>
    /// Read-only, so a fast <c>CreateSnapshot</c> job can refuse a
    /// misconfigured VM synchronously, rather than leaving the caller to
    /// discover the same fact from a long-running copy job that nothing
    /// polls. <see cref="CreateCheckpointAsync"/> still checks this itself
    /// before taking a checkpoint - this method exists beside it, not instead
    /// of it, for a caller that wants the answer before committing to that
    /// call at all.
    /// </remarks>
    /// <exception cref="VmNotOnHostException">The VM is not registered on this host.</exception>
    Task<bool> CanCheckpointAsync(string hostName, string vmId, CancellationToken cancellationToken);

    /// <summary>
    /// Whether the VM's configuration currently references
    /// <paramref name="vhdxPath"/> directly, or does not reference anything
    /// built on it at all - true either way. False only while a differencing
    /// disk is still stacked on top of it.
    /// </summary>
    /// <remarks>
    /// The post-merge wait's predicate. <see cref="DestroyCheckpointAsync"/>
    /// is fire-and-forget and returns once the merge has *started*, and -
    /// measured, per the comment already in <c>ClassifyAttachmentAsync</c>'s
    /// own retry loop - the checkpoint's configuration object can disappear a
    /// moment *before* the VM's disk actually re-points to the base. So "the
    /// checkpoint object is gone" is not the same question as "the chain has
    /// collapsed", and only the second one tells a caller it is safe to
    /// release its hold on the VM.
    /// <para>
    /// Deliberately non-throwing and deliberately not judging ownership,
    /// unlike <see cref="ClassifyAttachmentAsync"/>: that method's callers -
    /// attach, detach, expand, snapshot - each have a specific reason to
    /// treat an unresolved or foreign chain as a hard failure requiring an
    /// operator. This method's one caller only ever asks whether the chain
    /// it already knows about, from a merge it already started, has finished
    /// collapsing yet - an unresolved chain simply means "not collapsed yet",
    /// not "something is wrong", so this returns false rather than throwing.
    /// </para>
    /// </remarks>
    /// <exception cref="VmNotOnHostException">The VM is not registered on this host.</exception>
    Task<bool> IsChainCollapsedAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken);
}
