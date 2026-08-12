using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace HyperVCsiAgent.Installer.Actions;

/// <summary>
/// Deferred, elevated custom action: <c>ServiceInstall</c> registers the
/// service under <c>SERVICEACCOUNT</c> but never grants it the right to log
/// on as a service, and Windows does not grant that just because an account
/// owns a service - a domain account provisioned for this alone almost never
/// already holds it. Runs before the InstallServices standard action creates
/// the service, so the right already exists by the time StartServices tries
/// to start it.
/// </summary>
/// <remarks>
/// Without this, the install rolls back with MSI error 1920 while the
/// System event log records a Service Control Manager 7041 event: "the user
/// has not been granted the requested logon type at this computer."
/// LsaAddAccountRights has no managed wrapper, hence the P/Invoke.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class GrantServiceLogonRightCommand
{
    private const string SeServiceLogonRight = "SeServiceLogonRight";

    private const uint PolicyCreateAccount = 0x0010;
    private const uint PolicyLookupNames = 0x0800;

    public static int Run(string[] args)
    {
        var parsed = CommandLineArgs.Parse(args);
        var account = parsed.Require("account");

        var sid = AccountLookup.ResolveSid(account);
        if (sid is null)
        {
            Console.Error.WriteLine($"Could not resolve '{account}' to a security identifier. Is the account name correct and the domain reachable?");
            return 1;
        }

        var sidBytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(sidBytes, 0);

        var objectAttributes = new LSA_OBJECT_ATTRIBUTES { Length = Marshal.SizeOf<LSA_OBJECT_ATTRIBUTES>() };
        var openStatus = LsaOpenPolicy(IntPtr.Zero, ref objectAttributes, PolicyCreateAccount | PolicyLookupNames, out var policyHandle);
        if (openStatus != 0)
        {
            Console.Error.WriteLine($"Failed to open the local security policy: {DescribeNtStatus(openStatus)}");
            return 1;
        }

        var right = ToLsaString(SeServiceLogonRight);
        try
        {
            var addStatus = LsaAddAccountRights(policyHandle, sidBytes, [right], 1);
            if (addStatus != 0)
            {
                Console.Error.WriteLine($"Failed to grant '{account}' the 'Log on as a service' right: {DescribeNtStatus(addStatus)}");
                return 1;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(right.Buffer);
            LsaClose(policyHandle);
        }

        Console.WriteLine($"Granted '{account}' the 'Log on as a service' right.");
        return 0;
    }

    private static string DescribeNtStatus(uint status) => new Win32Exception(LsaNtStatusToWinError(status)).Message;

    private static LSA_UNICODE_STRING ToLsaString(string value) => new()
    {
        Buffer = Marshal.StringToHGlobalUni(value),
        Length = (ushort)(value.Length * 2),
        MaximumLength = (ushort)((value.Length + 1) * 2),
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct LSA_UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LSA_OBJECT_ATTRIBUTES
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public int Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [DllImport("advapi32.dll")]
    private static extern uint LsaOpenPolicy(IntPtr systemName, ref LSA_OBJECT_ATTRIBUTES objectAttributes, uint desiredAccess, out IntPtr policyHandle);

    [DllImport("advapi32.dll")]
    private static extern uint LsaAddAccountRights(IntPtr policyHandle, byte[] accountSid, LSA_UNICODE_STRING[] userRights, uint countOfRights);

    [DllImport("advapi32.dll")]
    private static extern uint LsaClose(IntPtr objectHandle);

    [DllImport("advapi32.dll")]
    private static extern int LsaNtStatusToWinError(uint status);
}
