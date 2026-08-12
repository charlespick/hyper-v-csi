using HyperVCsiAgent.Core.Tests;

namespace HyperVCsiAgent.Installer.Actions.Tests;

/// <summary>
/// Granting "Log on as a service" is a machine-wide LSA policy change that
/// needs an elevated token regardless of which account is being granted it -
/// unlike <see cref="GrantCertificateAccessCommand"/>'s file ACL, there is no
/// unprivileged scope (like CurrentUser) this can fall back to for a test run
/// that isn't administrator, so only the SID-resolution failure path - which
/// never reaches the privileged call - is exercised here.
/// </summary>
public sealed class GrantServiceLogonRightCommandTests
{
    [WindowsOnlyFact]
    public void GrantServiceLogonRight_UnknownAccount_Fails()
    {
        var result = GrantServiceLogonRightCommand.Run([
            "--account", "NOSUCHDOMAIN\\no-such-account-hyperv-csi-test",
        ]);

        Assert.Equal(1, result);
    }
}
