namespace HyperVCsiAgent.Core.HostControl;

/// <summary>
/// A held-open disk's properties as <see cref="IVhdxLocationService.ReadThroughHolderAsync"/>
/// read them, and the node whose vmms answered.
/// </summary>
public sealed record HeldDiskInfo(string HostName, HostDiskInfo Info);
