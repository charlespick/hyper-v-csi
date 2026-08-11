using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;

namespace HyperVCsiAgent.Installer.Actions;

/// <summary>
/// Deferred, elevated custom action: the agent's service account can only
/// present the server certificate in a TLS handshake if it can read that
/// certificate's private key, and Windows does not grant that just because
/// the account can see the certificate in the store. Runs after both the
/// service account and the certificate are known.
/// </summary>
/// <remarks>
/// The private key itself is not in the certificate the store hands back -
/// it is a separate file on disk, named by an opaque key container name and
/// located differently depending on which of the two key storage providers
/// created it: CNG (the default for anything issued or imported on a modern
/// Windows since roughly Server 2012) keeps it under
/// <c>ProgramData\Microsoft\Crypto\Keys</c> for a machine-keyset key, while
/// the legacy CAPI provider keeps it under
/// <c>ProgramData\Microsoft\Crypto\RSA\MachineKeys</c>. Getting this wrong
/// means the grant silently lands on nothing and the handshake keeps
/// failing with no indication why - hence handling both explicitly rather
/// than assuming whichever one a hand-generated test certificate happened to
/// use.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class GrantCertificateAccessCommand
{
    public static int Run(string[] args)
    {
        var parsed = CommandLineArgs.Parse(args);
        var thumbprint = parsed.Require("thumbprint");
        var storeName = parsed.Require("store-name");
        var storeLocation = parsed.Require("store-location");
        var account = parsed.Require("account");

        var certificate = CertificateLookup.Find(thumbprint, storeName, storeLocation);
        if (certificate is null)
        {
            Console.Error.WriteLine($"No certificate with thumbprint '{thumbprint}' was found in {storeLocation}\\{storeName}.");
            return 1;
        }

        var keyFilePath = ResolvePrivateKeyFilePath(certificate);
        if (keyFilePath is null)
        {
            Console.Error.WriteLine(
                $"Could not locate a private key file for certificate '{certificate.Subject}'. " +
                "It may not have an exportable/accessible private key on this host, or use a key storage provider this installer does not recognize.");
            return 1;
        }

        if (!File.Exists(keyFilePath))
        {
            Console.Error.WriteLine($"Resolved private key path '{keyFilePath}' does not exist.");
            return 1;
        }

        var sid = ResolveSid(account);
        if (sid is null)
        {
            Console.Error.WriteLine($"Could not resolve '{account}' to a security identifier. Is the account name correct and the domain reachable?");
            return 1;
        }

        var fileInfo = new FileInfo(keyFilePath);
        var security = fileInfo.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.Read, AccessControlType.Allow));
        fileInfo.SetAccessControl(security);

        Console.WriteLine($"Granted '{account}' read access to the private key for '{certificate.Subject}'.");
        return 0;
    }

    private static string? ResolvePrivateKeyFilePath(X509Certificate2 certificate)
    {
        using var rsa = certificate.GetRSAPrivateKey();
        return rsa switch
        {
            RSACng cng => ResolveCngKeyFilePath(cng.Key),
            RSACryptoServiceProvider csp => ResolveCapiKeyFilePath(csp.CspKeyContainerInfo),
            _ => null,
        };
    }

    private static string? ResolveCngKeyFilePath(CngKey key)
    {
        if (key.UniqueName is not { } uniqueName)
        {
            return null;
        }

        var root = key.IsMachineKey
            ? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
            : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(root, "Microsoft", "Crypto", "Keys", uniqueName);
    }

    private static string ResolveCapiKeyFilePath(CspKeyContainerInfo info)
    {
        var root = info.MachineKeyStore
            ? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
            : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(root, "Microsoft", "Crypto", "RSA", "MachineKeys", info.UniqueKeyContainerName);
    }

    private static SecurityIdentifier? ResolveSid(string account)
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
