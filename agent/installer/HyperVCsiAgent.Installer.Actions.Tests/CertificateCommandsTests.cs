using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using HyperVCsiAgent.Core.Tests;

namespace HyperVCsiAgent.Installer.Actions.Tests;

/// <summary>
/// Exercises <see cref="ValidateCertificateCommand"/> and
/// <see cref="GrantCertificateAccessCommand"/> against a real, disposable
/// self-signed certificate in <c>CurrentUser\My</c> - installing into
/// <c>LocalMachine</c> the way the agent's own certificate lives needs
/// administrator rights this test run cannot assume, but the certificate
/// lookup and CNG private-key-file resolution work identically regardless of
/// which store location holds the certificate.
/// </summary>
public sealed class CertificateCommandsTests : IDisposable
{
    private const string StoreNameArg = "My";
    private const string StoreLocationArg = "CurrentUser";

    private readonly X509Certificate2 _certificate;
    private readonly X509Store _store;

    public CertificateCommandsTests()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=hyperv-csi-agent-installer-actions-test",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(30));

        // CreateSelfSigned's certificate carries an ephemeral (in-memory-only)
        // key; re-importing it as Exportable + persisted is what actually puts
        // a CNG key file on disk under the current profile's key store, the
        // same shape GrantCertificateAccessCommand has to resolve for a real,
        // store-installed certificate.
        _certificate = X509CertificateLoader.LoadPkcs12(
            ephemeral.Export(X509ContentType.Pfx),
            password: null,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);

        _store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        _store.Open(OpenFlags.ReadWrite);
        _store.Add(_certificate);
    }

    public void Dispose()
    {
        _store.Remove(_certificate);
        _store.Dispose();
        _certificate.Dispose();
    }

    [WindowsOnlyFact]
    public void ValidateCertificate_FindsAnInstalledCertificateByThumbprint()
    {
        var result = ValidateCertificateCommand.Run([
            "--thumbprint", _certificate.Thumbprint,
            "--store-name", StoreNameArg,
            "--store-location", StoreLocationArg,
        ]);

        Assert.Equal(0, result);
    }

    [WindowsOnlyFact]
    public void ValidateCertificate_UnknownThumbprint_Fails()
    {
        var result = ValidateCertificateCommand.Run([
            "--thumbprint", "A1B2C3D4E5F60718293A4B5C6D7E8F90A1B2C3D4",
            "--store-name", StoreNameArg,
            "--store-location", StoreLocationArg,
        ]);

        Assert.Equal(1, result);
    }

    [WindowsOnlyFact]
    public void GrantCertificateAccess_AddsAReadRuleToTheActualPrivateKeyFile()
    {
        var account = WindowsIdentity.GetCurrent().Name;

        var result = GrantCertificateAccessCommand.Run([
            "--thumbprint", _certificate.Thumbprint,
            "--store-name", StoreNameArg,
            "--store-location", StoreLocationArg,
            "--account", account,
        ]);

        Assert.Equal(0, result);

        using var rsa = _certificate.GetRSAPrivateKey() as RSACng;
        Assert.NotNull(rsa);
        var keyPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Crypto", "Keys", rsa!.Key.UniqueName!);
        var acl = new FileInfo(keyPath).GetAccessControl();
        var sid = (SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier));
        Assert.Contains(
            acl.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>(),
            rule => rule.IdentityReference.Equals(sid) &&
                    rule.AccessControlType == AccessControlType.Allow &&
                    rule.FileSystemRights.HasFlag(FileSystemRights.Read));
    }

    [WindowsOnlyFact]
    public void GrantCertificateAccess_UnknownAccount_Fails()
    {
        var result = GrantCertificateAccessCommand.Run([
            "--thumbprint", _certificate.Thumbprint,
            "--store-name", StoreNameArg,
            "--store-location", StoreLocationArg,
            "--account", "NOSUCHDOMAIN\\no-such-account-hyperv-csi-test",
        ]);

        Assert.Equal(1, result);
    }
}
