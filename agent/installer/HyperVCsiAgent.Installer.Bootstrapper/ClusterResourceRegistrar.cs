using System;
using System.Linq;
using System.Runtime.Versioning;
using Microsoft.Management.Infrastructure;

namespace HyperVCsiAgent.Installer.Bootstrapper;

/// <summary>
/// Registers the agent as a clustered Generic Service resource - the
/// Clustering page's "add the role to the cluster" checkbox. Needs
/// cluster-admin rights, unlike everything else this wizard does.
/// </summary>
/// <remarks>
/// Deliberately creates the resource with no explicit dependency
/// (MSCluster_Resource.AddDependency is never called): the installer has no
/// reliable way to know which specific storage or network resource backs
/// the shared storage an operator pointed the Storage page at - guessing
/// one would risk wiring a dependency on the wrong resource, which is worse
/// than wiring none. A Generic Service resource does not require a
/// dependency to function; if a specific topology needs one, that is a
/// follow-up an operator makes in Failover Cluster Manager, not something
/// this wizard should invent.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class ClusterResourceRegistrar
{
    private const string NamespaceName = @"root\MSCluster";
    private const string GroupName = "Hyper-V CSI Agent";
    private const string ResourceName = "Hyper-V CSI Agent";
    private const string ServiceName = "hyperv-csi-agent";

    // MSCluster_ResourceGroup.GroupType: Generic Service.
    private const uint GenericServiceGroupType = 108;

    private const uint BringOnlineTimeoutSeconds = 60;

    public static void Register()
    {
        using var session = CimSession.Create(null);

        // Idempotent: running this installer again on a node that already
        // registered the role - or that Failover Cluster Manager already
        // knows about from another node - must not create a duplicate.
        var resourceExists = ResourceExists(session, ResourceName);
        var groupExists = GroupExists(session, GroupName);

        if (resourceExists)
        {
            return;
        }

        if (groupExists)
        {
            // A group with this name but no resource in it means a
            // previous attempt got partway through and stopped - most
            // likely CreateGroup succeeded and CreateResource then failed.
            // Retrying CreateGroup would just throw a confusing "group
            // already exists" error from the provider, so this fails with
            // an explicit, actionable one instead of leaving the operator
            // to guess.
            throw new InvalidOperationException(
                $"a cluster group named '{GroupName}' already exists but has no '{ResourceName}' resource in it - " +
                "a previous attempt to register this role likely failed partway through. Remove the group in " +
                "Failover Cluster Manager and try again.");
        }

        CreateGroup(session);
        var resourceId = CreateResource(session);
        var resource = FindResourceById(session, resourceId);

        // Order matters: BringOnline before ServiceName is set would try to
        // start a Generic Service resource that does not yet know which
        // service to manage.
        SetServiceName(session, resource);
        BringOnline(session, resource);
    }

    private static bool ResourceExists(CimSession session, string resourceName) =>
        session.QueryInstances(NamespaceName, "WQL", $"SELECT Name FROM MSCluster_Resource WHERE Name = '{EscapeLiteral(resourceName)}'").Any();

    private static bool GroupExists(CimSession session, string groupName) =>
        session.QueryInstances(NamespaceName, "WQL", $"SELECT Name FROM MSCluster_ResourceGroup WHERE Name = '{EscapeLiteral(groupName)}'").Any();

    private static void CreateGroup(CimSession session)
    {
        var parameters = new CimMethodParametersCollection
        {
            CimMethodParameter.Create("GroupName", GroupName, CimType.String, CimFlags.In),
            CimMethodParameter.Create("GroupType", GenericServiceGroupType, CimType.UInt32, CimFlags.In),
            CimMethodParameter.Create("Id", string.Empty, CimType.String, CimFlags.In | CimFlags.Out),
        };

        session.InvokeMethod(NamespaceName, "MSCluster_ResourceGroup", "CreateGroup", parameters);
    }

    private static string CreateResource(CimSession session)
    {
        var parameters = new CimMethodParametersCollection
        {
            CimMethodParameter.Create("Group", GroupName, CimType.String, CimFlags.In),
            CimMethodParameter.Create("ResourceName", ResourceName, CimType.String, CimFlags.In),
            CimMethodParameter.Create("ResourceType", "Generic Service", CimType.String, CimFlags.In),
            CimMethodParameter.Create("SeparateMonitor", false, CimType.Boolean, CimFlags.In),
            CimMethodParameter.Create("Id", string.Empty, CimType.String, CimFlags.In | CimFlags.Out),
        };

        var result = session.InvokeMethod(NamespaceName, "MSCluster_Resource", "CreateResource", parameters);
        return (string)result.OutParameters["Id"].Value;
    }

    private static CimInstance FindResourceById(CimSession session, string resourceId) =>
        session.QueryInstances(NamespaceName, "WQL", $"SELECT * FROM MSCluster_Resource WHERE Id = '{EscapeLiteral(resourceId)}'").First();

    // SetPrivateProperties does not appear on MSCluster_Resource's own MOF-
    // derived documentation page, because - unlike CreateResource or
    // BringOnline - its parameter schema is dynamic, generated per
    // resource type rather than statically declared. This is the same
    // method (and the same "spawn an in-params instance, set the
    // resource-type-specific field, invoke" shape) classic WMI/VBScript
    // cluster automation has used since Windows Server 2003 to set a
    // Generic Service resource's ServiceName - CimMethodParametersCollection
    // is CIM's typed equivalent of that spawned in-params object.
    private static void SetServiceName(CimSession session, CimInstance resource)
    {
        var parameters = new CimMethodParametersCollection
        {
            CimMethodParameter.Create("ServiceName", ServiceName, CimType.String, CimFlags.In),
        };

        session.InvokeMethod(NamespaceName, resource, "SetPrivateProperties", parameters);
    }

    private static void BringOnline(CimSession session, CimInstance resource)
    {
        var parameters = new CimMethodParametersCollection
        {
            CimMethodParameter.Create("TimeOut", BringOnlineTimeoutSeconds, CimType.UInt32, CimFlags.In),
        };

        session.InvokeMethod(NamespaceName, resource, "BringOnline", parameters);
    }

    private static string EscapeLiteral(string value) => value.Replace("'", "''");
}
