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
}
