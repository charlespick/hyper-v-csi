using System.Runtime.Versioning;

namespace HyperVCsiAgent.Installer.Actions;

/// <summary>
/// Deferred, elevated custom action: opens an inbound TCP rule for the
/// agent's configured HTTPS port. Without one, Windows Firewall's default
/// inbound policy silently drops unsolicited traffic from anything but this
/// host's own subnet - the client sees a connection timeout, not a clear
/// refusal, which is a hard failure mode to place without knowing to look at
/// the firewall specifically.
/// </summary>
/// <remarks>
/// Uses the Windows Firewall COM API (HNetCfg.FwPolicy2/HNetCfg.FWRule) via
/// <c>dynamic</c> late binding rather than hand-declared [ComImport]
/// interfaces - the real INetFwPolicy2/INetFwRule vtable member order is not
/// something worth risking a transcription mistake on, and late binding
/// resolves everything by name instead.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class OpenFirewallPortCommand
{
    // Shared with CloseFirewallPortCommand, which removes whatever this adds.
    internal const string RuleName = "Hyper-V CSI Agent";
    private const int ProtocolTcp = 6;
    private const int DirectionInbound = 1;
    private const int ActionAllow = 1;

    public static int Run(string[] args)
    {
        var parsed = CommandLineArgs.Parse(args);
        var port = parsed.Require("port");

        var policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2")
            ?? throw new InvalidOperationException("Windows Firewall COM policy object (HNetCfg.FwPolicy2) is not registered on this host.");
        dynamic policy = Activator.CreateInstance(policyType)!;

        // Idempotent: drop any rule this installer left behind under the same
        // name before adding the current one, rather than accumulating a
        // duplicate on every repair or reinstall. Remove throws if the name
        // is not registered yet - the expected case on a first install - so
        // that failure is swallowed rather than treated as an error.
        try
        {
            policy.Rules.Remove(RuleName);
        }
        catch
        {
            // Not present yet - nothing to remove.
        }

        var ruleType = Type.GetTypeFromProgID("HNetCfg.FWRule")
            ?? throw new InvalidOperationException("Windows Firewall COM rule object (HNetCfg.FWRule) is not registered on this host.");
        dynamic rule = Activator.CreateInstance(ruleType)!;
        rule.Name = RuleName;
        rule.Description = "Inbound HTTPS access to the Hyper-V CSI Agent's job API.";
        rule.Protocol = ProtocolTcp;
        rule.LocalPorts = port;
        rule.Direction = DirectionInbound;
        rule.Action = ActionAllow;
        rule.Enabled = true;

        policy.Rules.Add(rule);

        Console.WriteLine($"Opened inbound TCP port {port} for '{RuleName}'.");
        return 0;
    }
}
