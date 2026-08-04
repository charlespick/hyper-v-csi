namespace HyperVCsiAgent.Core.HostControl;

/// <summary>
/// The VM isn't registered on the host we were told owns it. Distinct from any
/// other CIM failure because it has a specific and expected cause: the VM live
/// migrated between resolving its owner and the call landing, which the design
/// anticipates rather than treats as a fault. The caller re-resolves ownership
/// and tries once more.
/// </summary>
public sealed class VmNotOnHostException(string hostName, string vmName)
    : Exception($"VM {vmName} is not registered on {hostName}; it has probably migrated")
{
    public string HostName { get; } = hostName;

    public string VmName { get; } = vmName;
}
