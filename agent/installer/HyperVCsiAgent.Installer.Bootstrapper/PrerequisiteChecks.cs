using System.Runtime.Versioning;
using Microsoft.Management.Infrastructure;
using Microsoft.Win32;

namespace HyperVCsiAgent.Installer.Bootstrapper;

/// <summary>
/// The Prerequisites page's two live checks. Both are read-only, local, and
/// need no elevation beyond what an interactive setup already has.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class PrerequisiteChecks
{
    /// <summary>
    /// Same signal MsClusterService's own ResourcesKeyPath reads for a
    /// running agent, checked here for existence only rather than parsed -
    /// the wizard needs a yes/no, not the resource table itself.
    /// </summary>
    private const string ClusterResourcesKeyPath = @"Cluster\Resources";

    private const string HyperVFeatureName = "Microsoft-Hyper-V";

    public static PrerequisiteCheckResult CheckHyperVRole()
    {
        // Win32_OptionalFeature.InstallState: 1 Enabled, 2 Disabled, 3 Absent.
        using var session = CimSession.Create(null);
        var query = $"SELECT InstallState FROM Win32_OptionalFeature WHERE Name = '{HyperVFeatureName}'";
        foreach (var feature in session.QueryInstances(@"root\cimv2", "WQL", query))
        {
            if (feature.CimInstanceProperties["InstallState"]?.Value is uint state && state == 1)
            {
                return new PrerequisiteCheckResult("Hyper-V role", PrerequisiteStatus.Pass, "Installed.");
            }
        }

        return new PrerequisiteCheckResult(
            "Hyper-V role", PrerequisiteStatus.Warn,
            "Not installed on this host. The agent needs the Hyper-V role to manage VMs here.");
    }

    public static PrerequisiteCheckResult CheckClusterMembership()
    {
        using var resources = Registry.LocalMachine.OpenSubKey(ClusterResourcesKeyPath);
        return resources is not null
            ? new PrerequisiteCheckResult("Failover cluster membership", PrerequisiteStatus.Pass, "This host is a cluster member.")
            : new PrerequisiteCheckResult(
                "Failover cluster membership", PrerequisiteStatus.Warn,
                "This host is not a failover cluster member. Shared storage must still be reachable from every node that will run this agent.");
    }
}
