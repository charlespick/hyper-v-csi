using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace HyperVCsiAgent.Installer.Actions;

/// <summary>
/// Deferred, elevated custom action: deletes the <c>hyperv-csi-agent</c>
/// service. <c>Product.wxs</c>'s <c>ServiceControl</c> no longer carries
/// <c>Remove="uninstall"</c> (see its own remarks), so MSI won't delete the
/// service on its own; this is scheduled in its place
/// <c>Before="RemoveFiles" Condition="REMOVE AND NOT UPGRADINGPRODUCTCODE"</c>,
/// the same guard <see cref="CloseFirewallPortCommand"/> uses for the same
/// reason - a named system object MSI's component reference-counting can't
/// see, so a chained major-upgrade removal must skip it.
/// </summary>
/// <remarks>
/// P/Invoke (<c>OpenSCManager</c>/<c>OpenService</c>/<c>DeleteService</c>),
/// not <c>sc.exe</c> - see <see cref="GrantServiceLogonRightCommand"/>'s
/// remarks for why this repo prefers a direct API over parsing a localised
/// exit code. An absent service is a no-op success, not a failure -
/// <c>ERROR_SERVICE_DOES_NOT_EXIST</c> (1060) is checked separately so an
/// unattended install that never got far enough to create the service still
/// uninstalls cleanly. Runs after the standard <c>StopServices</c> action
/// (still scheduled via <c>ServiceControl Stop="both"</c>), which minimises
/// but doesn't eliminate the window where <c>DeleteService</c> only marks
/// the service pending deletion because another handle is still open.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class RemoveAgentServiceCommand
{
    internal const string ServiceName = "hyperv-csi-agent";

    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceDelete = 0x00010000;
    private const int ErrorServiceDoesNotExist = 1060;

    public static int Run(string[] args)
    {
        var scManager = OpenSCManagerW(null, null, ScManagerConnect);
        if (scManager == IntPtr.Zero)
        {
            var lastError = Marshal.GetLastWin32Error();
            Console.Error.WriteLine($"Failed to open the Service Control Manager: {new Win32Exception(lastError).Message}");
            return 1;
        }

        try
        {
            var service = OpenServiceW(scManager, ServiceName, ServiceDelete);
            if (service == IntPtr.Zero)
            {
                var lastError = Marshal.GetLastWin32Error();
                if (lastError == ErrorServiceDoesNotExist)
                {
                    Console.WriteLine($"No '{ServiceName}' service was present.");
                    return 0;
                }

                Console.Error.WriteLine($"Failed to open the '{ServiceName}' service for deletion: {new Win32Exception(lastError).Message}");
                return 1;
            }

            try
            {
                if (!DeleteService(service))
                {
                    var lastError = Marshal.GetLastWin32Error();
                    Console.Error.WriteLine($"Failed to delete the '{ServiceName}' service: {new Win32Exception(lastError).Message}");
                    return 1;
                }

                Console.WriteLine($"Deleted the '{ServiceName}' service.");
                return 0;
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(scManager);
        }
    }

    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManagerW(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenServiceW(IntPtr scManagerHandle, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DeleteService(IntPtr serviceHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
