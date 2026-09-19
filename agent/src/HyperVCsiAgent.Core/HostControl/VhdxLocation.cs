namespace HyperVCsiAgent.Core.HostControl;

/// <summary>
/// Where an open VHDX lives: the clustered VM whose storage references it, and
/// the node that VM runs on - the host whose vmms can answer about the file
/// while it is held open.
/// </summary>
/// <remarks>
/// Always both halves or nothing. A caller that needs only the host - to send
/// it a path-only CIM call - asks
/// <see cref="IVhdxLocationService.ReadThroughHolderAsync"/> instead, which
/// never pays for naming the VM.
/// </remarks>
public sealed record VhdxLocation(string HostName, string VmId);
