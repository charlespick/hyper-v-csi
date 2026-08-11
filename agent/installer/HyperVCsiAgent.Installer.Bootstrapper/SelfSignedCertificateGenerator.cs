using System;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace HyperVCsiAgent.Installer.Bootstrapper;

/// <summary>
/// Creates a new self-signed server certificate and imports it into
/// LocalMachine\My - the store and location the Certificate page's table
/// always reads, now that both stopped being user-editable.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class SelfSignedCertificateGenerator
{
    private const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";

    public static X509Certificate2 CreateAndImport(string subjectName)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            new X500DistinguishedName($"CN={subjectName}"), rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid(ServerAuthenticationOid)], critical: false));

        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName(subjectName);
        request.CertificateExtensions.Add(sanBuilder.Build());

        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        var notAfter = notBefore.AddYears(5);
        using var selfSigned = request.CreateSelfSigned(notBefore, notAfter);

        // Round-tripped through PKCS12 with a persisted (not ephemeral) key
        // so SChannel can use it as a TLS server credential later - see
        // StoreCertificateProvider's own remarks on why re-importing with
        // EphemeralKeySet produces a key SChannel refuses for that. The
        // password only exists to move the key through that round trip, so
        // both it and the PFX bytes are wiped as soon as the load is done
        // rather than left for the GC to reclaim on its own schedule.
        var password = Guid.NewGuid().ToString("N");
        byte[] pfxBytes = selfSigned.Export(X509ContentType.Pfx, password);
        X509Certificate2 persisted;
        try
        {
            persisted = X509CertificateLoader.LoadPkcs12(
                pfxBytes, password, X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet);
        }
        finally
        {
            Array.Clear(pfxBytes);
        }

        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        store.Add(persisted);

        return persisted;
    }
}
