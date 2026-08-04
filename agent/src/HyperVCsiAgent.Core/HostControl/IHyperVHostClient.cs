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
    Task<AttachedDisk?> FindAttachedDiskAsync(string hostName, string vmName, string vhdxPath, CancellationToken cancellationToken);

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
    Task<bool> IsDiskAttachedAsync(string hostName, string vmName, string vhdxPath, CancellationToken cancellationToken);

    /// <summary>
    /// Finds an unoccupied address on one of the VM's existing SCSI controllers,
    /// or null when every one of them is full.
    /// </summary>
    /// <exception cref="VmNotOnHostException">The VM is not registered on this host.</exception>
    Task<DiskSlot?> FindFreeSlotAsync(string hostName, string vmName, CancellationToken cancellationToken);

    /// <summary>
    /// Attaches the VHDX at the given slot.
    /// </summary>
    /// <exception cref="VmNotOnHostException">The VM is not registered on this host.</exception>
    Task AttachDiskAsync(string hostName, string vmName, string vhdxPath, DiskSlot slot, CancellationToken cancellationToken);

    Task DetachDiskAsync(string hostName, string vmName, string vhdxPath, CancellationToken cancellationToken);

    Task ResizeDiskAsync(string hostName, string vmName, string vhdxPath, long newSizeBytes, CancellationToken cancellationToken);
}
