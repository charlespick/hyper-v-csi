namespace HyperVCsiAgent.Core.HostControl;

/// <summary>
/// What <see cref="IHyperVHostClient.GetDiskInfoAsync"/> reads off a VHDX
/// through the host that has it open.
/// </summary>
/// <param name="VirtualSizeBytes"><c>MaxInternalSize</c>: the disk's virtual size.</param>
/// <param name="DiskId">
/// <c>VirtualDiskId</c>, the same identity <see cref="Storage.VhdxDiskIdentity"/>
/// reads out of the file itself - read this way instead when a running VM's hold
/// on the file keeps it from being opened locally.
/// </param>
public sealed record HostDiskInfo(long VirtualSizeBytes, Guid DiskId);
