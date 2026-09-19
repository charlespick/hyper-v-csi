namespace HyperVCsiAgent.Core.Storage;

/// <summary>
/// Thrown by <see cref="IVirtualDiskManager.GetVirtualSizeAsync"/> when the
/// VHDX's size could not be read because something already has the file open —
/// in practice, a running VM the disk is attached to, on whichever host
/// currently owns it. <see cref="VhdxService"/> treats this as "cannot tell
/// locally", not "cannot grow" or "cannot read": it traces the file to the host
/// holding it and asks that host instead, which does not share this limitation -
/// see <see cref="HyperVCsiAgent.Core.HostControl.IVhdxLocationService"/> and
/// <see cref="HyperVCsiAgent.Core.HostControl.IHyperVHostClient.GetDiskInfoAsync"/>.
/// </summary>
public sealed class VhdxInUseException(string path, Exception innerException)
    : Exception($"{path} could not be read because something else has it open, most likely a running VM with the disk attached", innerException)
{
    public string Path { get; } = path;
}
