using System.Diagnostics;

namespace HyperVCsiAgent.Installer.Actions;

/// <summary>
/// Deferred, unimpersonated custom action: registers the
/// <c>hyperv-csi-agent</c> Event Log source Program.cs's EventLog provider
/// writes under. Creating a source that does not yet exist needs registry
/// rights under <c>HKLM\SYSTEM\...\EventLog\Application</c> that
/// <c>[SERVICEACCOUNT]</c> - which is not necessarily LocalSystem, see
/// Product.wxs - cannot be relied on to have, and .NET's own
/// EventLogLoggerProvider swallows that failure silently rather than
/// throwing it into the Application log it could not create in the first
/// place. Registering it here instead, during the MSI's own elevated
/// execute sequence, means the source always exists by the time the service
/// first starts and logs anything, regardless of which account it runs as.
/// </summary>
/// <remarks>
/// No CustomActionData: unlike WriteConfigCommand, this needs no
/// wizard-collected value at all - the source name is fixed, the same one
/// Program.cs registers the provider under.
/// </remarks>
internal static class EnsureEventLogSourceCommand
{
    internal const string SourceName = "hyperv-csi-agent";
    private const string LogName = "Application";

    public static int Run(string[] args)
    {
        if (!EventLog.SourceExists(SourceName))
        {
            EventLog.CreateEventSource(SourceName, LogName);
            Console.WriteLine($"Created event source '{SourceName}' in the '{LogName}' log.");
        }
        else
        {
            Console.WriteLine($"Event source '{SourceName}' already exists.");
        }

        return 0;
    }
}
