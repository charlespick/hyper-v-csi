using HyperVCsiAgent.Core.Tests;

namespace HyperVCsiAgent.Installer.Actions.Tests;

/// <summary>
/// Tests for the <c>remove-service</c> command, alongside
/// <see cref="GrantServiceLogonRightCommandTests"/>'s own pattern.
/// </summary>
/// <remarks>
/// Only the absent-service no-op path is exercised here. Opening the Service
/// Control Manager with <c>SC_MANAGER_CONNECT</c>, and then
/// <c>OpenService</c>-ing a name that does not exist in the service database,
/// both succeed without elevation - the SCM reports
/// <c>ERROR_SERVICE_DOES_NOT_EXIST</c> for an unknown name before any
/// per-service access check ever runs, so this path needs no administrator
/// token. "Present service is deleted" is a case worth covering too, but -
/// unlike the absent-service path above - both creating a real Windows
/// service to delete and the deletion itself need an elevated token, the
/// same reason <see cref="GrantServiceLogonRightCommandTests"/> only
/// exercises its own unprivileged SID-resolution-failure branch and not the
/// privileged <c>LsaAddAccountRights</c> call itself. There is no
/// unprivileged fallback scope for a service object the way
/// <c>GrantCertificateAccessCommand</c> has one for a file ACL (CurrentUser),
/// so that case is left to manual, on-a-real-node validation rather than
/// faked here. Whether the command never runs at all when its guard
/// condition is false is authoring, not runtime behaviour, and is covered by
/// a package-inspection assertion instead.
/// </remarks>
public sealed class RemoveAgentServiceCommandTests
{
    [WindowsOnlyFact]
    public void RemoveAgentService_AbsentService_IsANoOpSuccess()
    {
        // "hyperv-csi-agent" is not expected to exist on the machine running
        // this test suite (a developer workstation or a CI runner, neither of
        // which has the agent installed) - this asserts the tolerance an
        // unattended install needs: one that never got as far as creating
        // the service must still uninstall cleanly.
        var result = RemoveAgentServiceCommand.Run([]);

        Assert.Equal(0, result);
    }
}
