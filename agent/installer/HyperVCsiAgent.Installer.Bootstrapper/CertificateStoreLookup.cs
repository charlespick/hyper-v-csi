using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;

namespace HyperVCsiAgent.Installer.Bootstrapper;

/// <summary>
/// Lists candidate server certificates for the Certificate page's table.
/// Filters by the same two conditions CertificateSelector.Select applies
/// before it ever gets to thumbprint matching - has a private key, currently
/// within its validity window - since nothing is configured yet for that
/// method's own allowed-thumbprints filter to work against. Always
/// LocalMachine\My: the store and location stopped being user-editable in
/// this rework, so nothing else needs to be asked here.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class CertificateStoreLookup
{
    public static IReadOnlyList<CertificateEntry> ListCandidates()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);

        var now = DateTimeOffset.Now;
        return store.Certificates
            .OfType<X509Certificate2>()
            .Where(certificate => certificate.HasPrivateKey)
            .Where(certificate => now >= certificate.NotBefore && now <= certificate.NotAfter)
            .Select(certificate => new CertificateEntry(
                certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false),
                certificate.Thumbprint,
                certificate.NotAfter))
            .OrderByDescending(entry => entry.NotAfter)
            .ToList();
    }
}
