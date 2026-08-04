using System.Text.RegularExpressions;

namespace HyperVCsiAgent.Service.Cim;

/// <summary>
/// Guards the text that reaches a WQL query.
/// </summary>
public static partial class WqlNames
{
    /// <summary>
    /// The canonical 8-4-4-4-12 GUID form, which is what a node ID is: the VM's
    /// ID, read from the guest's key-value pools by the node plugin. A node ID
    /// no longer reaches a query - it is compared against the cluster database
    /// in memory - but rejecting anything else is still a real check, because a
    /// node ID that is not a GUID cannot identify a VM, and matching nothing
    /// would report that as "no such VM in the cluster".
    /// </summary>
    [GeneratedRegex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")]
    private static partial Regex Guid { get; }

    public static bool IsVmId(string value) => Guid.IsMatch(value);

    /// <summary>
    /// Escapes a WQL string literal. Cluster resource names are not
    /// caller-supplied, but they are free text - an apostrophe in one would
    /// otherwise close the literal early and produce a malformed query rather
    /// than a failed match. Backslash first, or the escapes this adds would
    /// themselves be escaped.
    /// </summary>
    public static string EscapeLiteral(string value) =>
        value.Replace(@"\", @"\\").Replace("'", @"\'");
}
