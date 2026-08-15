using System.Runtime.Versioning;

namespace HyperVCsiAgent.Installer.Actions;

/// <summary>
/// Deferred, elevated custom action: undoes OpenFirewallPortCommand on
/// uninstall. No port argument - the rule is found and removed by name, the
/// same way OpenFirewallPortCommand's own idempotent Remove works.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class CloseFirewallPortCommand
{
    public static int Run(string[] args)
    {
        var policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2")
            ?? throw new InvalidOperationException("Windows Firewall COM policy object (HNetCfg.FwPolicy2) is not registered on this host.");
        dynamic policy = Activator.CreateInstance(policyType)!;

        try
        {
            policy.Rules.Remove(OpenFirewallPortCommand.RuleName);
            Console.WriteLine($"Removed the '{OpenFirewallPortCommand.RuleName}' firewall rule.");
        }
        catch
        {
            // Already gone - an unattended install that never configured the
            // agent (see OpenFirewallPortCommand's own Condition) never
            // added it in the first place.
            Console.WriteLine($"No '{OpenFirewallPortCommand.RuleName}' firewall rule was present.");
        }

        return 0;
    }
}
