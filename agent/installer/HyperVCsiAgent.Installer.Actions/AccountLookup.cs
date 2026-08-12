using System.Security.Principal;

namespace HyperVCsiAgent.Installer.Actions;

/// <summary>
/// Shared by every custom action that needs to turn an operator-supplied
/// account name into a security identifier - resolution failure (typo'd
/// name, unreachable domain) is reported the same way regardless of which
/// command asked.
/// </summary>
internal static class AccountLookup
{
    public static SecurityIdentifier? ResolveSid(string account)
    {
        try
        {
            return (SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier));
        }
        catch (IdentityNotMappedException)
        {
            return null;
        }
    }
}
