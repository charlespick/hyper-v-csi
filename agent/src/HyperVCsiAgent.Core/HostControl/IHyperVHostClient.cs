namespace HyperVCsiAgent.Core.HostControl;

/// <summary>
/// WinRM/CIM (root\virtualization\v2) calls targeted at the host resolved by
/// IClusterService as the current VM owner. The agent is the only thing ever
/// permitted to initiate this - no Kubernetes component, and no other host, ever does.
/// </summary>
public interface IHyperVHostClient
{
    Task AttachDiskAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken);

    Task DetachDiskAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken);

    Task ResizeDiskAsync(string hostName, string vmId, string vhdxPath, long newSizeBytes, CancellationToken cancellationToken);
}
