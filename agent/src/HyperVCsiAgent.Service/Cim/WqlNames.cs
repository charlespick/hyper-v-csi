using System.Text.RegularExpressions;

namespace HyperVCsiAgent.Service.Cim;

/// <summary>
/// Guards the text that reaches a WQL query.
/// </summary>
public static partial class WqlNames
{
    /// <summary>
    /// The canonical 8-4-4-4-12 GUID form, which is what a node ID is: the VM's
    /// ID, as discovery recorded it on the Node object. A node ID
    /// no longer reaches a query - it is compared against the cluster database
    /// in memory - but rejecting anything else is still a real check, because a
    /// node ID that is not a GUID cannot identify a VM, and matching nothing
    /// would report that as "no such VM in the cluster".
    /// </summary>
    [GeneratedRegex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")]
    private static partial Regex Guid { get; }

    public static bool IsVmId(string value) => Guid.IsMatch(value);

    /// <summary>
    /// The same 8-4-4-4-12 form, for a VM's SMBIOS UUID (<c>BIOSGUID</c>). Unlike
    /// a node ID this one is interpolated into a WQL filter - nothing indexes
    /// <c>BIOSGUID</c> in memory - so the pattern is what keeps the literal a
    /// GUID and nothing else. Braces are rejected rather than tolerated: vmms
    /// stores the value bare, and a braced literal silently matches nothing.
    /// </summary>
    public static bool IsBiosGuid(string value) => Guid.IsMatch(value);

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
