using System.Text.RegularExpressions;

namespace HyperVCsiAgent.Service.Cim;

/// <summary>
/// Guards the one place caller-supplied text reaches a WQL query: the CSI node
/// ID, which arrives over the job API and ends up in a WHERE clause.
/// </summary>
public static partial class WqlNames
{
    /// <summary>
    /// The canonical 8-4-4-4-12 GUID form, which is what a node ID is: the VM's
    /// ID, read from the guest's key-value pools by the node plugin. Rejecting
    /// anything else is both the injection guard and a real check - a node ID
    /// that is not a GUID cannot identify a VM, so there is nothing to gain by
    /// escaping it and passing it to a query that will not match.
    /// </summary>
    [GeneratedRegex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")]
    private static partial Regex Guid { get; }

    public static bool IsVmId(string value) => Guid.IsMatch(value);
}
