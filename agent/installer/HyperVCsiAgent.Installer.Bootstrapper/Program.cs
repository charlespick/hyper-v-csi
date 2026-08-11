using System;
using WixToolset.BootstrapperApplicationApi;

namespace HyperVCsiAgent.Installer.Bootstrapper;

internal static class Program
{
    // No [STAThread] here: ManagedBootstrapperApplication.Run initializes
    // this thread as MTA internally (native BootstrapperApplicationRun
    // calls CoInitializeEx itself). Marking Main STA makes the CLR
    // initialize COM first, and the native call then fails with
    // RPC_E_CHANGED_MODE trying to switch apartments on an already-
    // initialized thread. WPF still needs an STA thread of its own - see
    // BootstrapperApp.Run for where that gets spun up.
    private static int Main()
    {
        var application = new BootstrapperApp();
        ManagedBootstrapperApplication.Run(application);
        return application.ExitCode;
    }
}
