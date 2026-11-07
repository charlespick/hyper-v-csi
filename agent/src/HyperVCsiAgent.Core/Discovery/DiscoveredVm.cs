namespace HyperVCsiAgent.Core.Discovery;

/// <summary>
/// The VM a node's SMBIOS UUID was found on, as <c>GET /v1/vm-discovery/{biosGuid}</c>
/// serializes it. Field names are pinned by <c>VmDiscoveryEndpointTests</c> and
/// mirrored by <c>DiscoveredVM</c> in csi-driver/internal/agentclient/client.go;
/// change them together.
/// </summary>
/// <param name="BiosGuid">The SMBIOS UUID that was searched for, echoed back.</param>
/// <param name="VmId">
/// The VM's ID in lowercase 8-4-4-4-12 form: <c>Msvm_ComputerSystem.Name</c>,
/// <c>Msvm_VirtualSystemSettingData.ConfigurationID</c> and the cluster
/// database's <c>VmID</c>. This is what the node plugin reports as its CSI node
/// ID once the controller has recorded it on the Node object.
/// </param>
/// <param name="HostName">
/// The host that reported the match. Informational only: a VM moves between
/// hosts, and every operation resolves its owner afresh from the cluster
/// database rather than trusting where discovery happened to find it.
/// </param>
public sealed record DiscoveredVm(string BiosGuid, string VmId, string HostName);
