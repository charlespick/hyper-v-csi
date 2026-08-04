using System.Text.RegularExpressions;

namespace HyperVCsiAgent.Service.Cim;

/// <summary>
/// Guards the one place caller-supplied text reaches a WQL query: the node ID,
/// which arrives over the job API and ends up in a WHERE clause.
/// </summary>
public static partial class WqlNames
{
    /// <summary>
    /// A hostname-shaped name, which is what a Kubernetes node ID is. Rejecting
    /// rather than escaping is deliberate: a name outside this shape cannot be a
    /// node in this cluster anyway, so there is nothing to gain by carefully
    /// passing it through to a query that will not match.
    /// </summary>
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,252}$")]
    private static partial Regex SafeName { get; }

    public static bool IsSafe(string name) => SafeName.IsMatch(name);
}
