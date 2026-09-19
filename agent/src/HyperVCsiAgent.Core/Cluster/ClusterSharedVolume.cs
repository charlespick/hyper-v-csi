namespace HyperVCsiAgent.Core.Cluster;

/// <summary>
/// One Cluster Shared Volume, as the cluster database describes it.
/// </summary>
/// <param name="Path">
/// Where the volume is mounted on every node, e.g. <c>C:\ClusterStorage\Volume1</c>,
/// with no trailing separator. <c>MSCluster_ClusterSharedVolume.Name</c> verbatim.
/// </param>
/// <param name="CoordinatorNode">
/// The node the volume's file system is actually mounted on - the owner of its
/// physical disk resource. Every other node's metadata operations on the volume
/// are forwarded here, which is what makes it the one node able to list them.
/// </param>
public sealed record ClusterSharedVolume(string Path, string CoordinatorNode);
