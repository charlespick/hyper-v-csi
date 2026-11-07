namespace HyperVCsiAgent.Core.Discovery;

/// <summary>
/// Discovery found no VM, but did not hear a complete answer from the cluster
/// either: the node list could not be read, or at least one node that is up
/// failed to answer. Retryable, and never the same as "no such VM" - the VM may
/// well be running on exactly the host that did not answer.
/// </summary>
public sealed class VmDiscoveryIncompleteException(string message, IReadOnlyList<string> unansweredHosts, Exception? innerException = null)
    : Exception(message, innerException)
{
    /// <summary>The hosts that were asked and did not answer. Empty when the node list itself was the problem.</summary>
    public IReadOnlyList<string> UnansweredHosts { get; } = unansweredHosts;
}
