namespace HyperVCsiAgent.Core.HostControl;

/// <summary>
/// Where an open VHDX lives: the clustered VM whose storage references it, and
/// the node that VM runs on - the host whose vmms can answer about the file
/// while it is held open.
/// </summary>
/// <remarks>
/// Always both halves or nothing. A host is only worth sending a path-only CIM
/// call to once a VM registered there has been confirmed to reference the path;
/// a node that merely has the file open - another reader, this agent's own copy -
/// cannot answer for it any better than the node the caller is already on.
/// </remarks>
public sealed record VhdxLocation(string HostName, string VmId);
