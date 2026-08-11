using System.Security.Cryptography.X509Certificates;
using HyperVCsiAgent.Core.Configuration;
using HyperVCsiAgent.Core.Security;

namespace HyperVCsiAgent.Installer.Actions;

/// <summary>
/// Opens the same store/location the agent itself reads at startup, using
/// <see cref="TlsOptions.ResolveStore"/> so a typo'd store name is rejected
/// with the exact same message an operator would see from the agent, not a
/// second wording invented for the installer.
/// </summary>
internal static class CertificateLookup
{
    public static X509Certificate2? Find(string thumbprint, string storeName, string storeLocation)
    {
        var normalized = ClientCertificateAuthenticator.Normalize(thumbprint);
        if (!ClientCertificateAuthenticator.IsWellFormed(normalized))
        {
            throw new ArgumentException(
                $"'{thumbprint}' is not a SHA-1 thumbprint: expected {ClientCertificateAuthenticator.ThumbprintLength} hex characters, got {normalized.Length}");
        }

        var (name, location) = new TlsOptions { StoreName = storeName, StoreLocation = storeLocation }.ResolveStore();

        using var store = new X509Store(name, location);
        store.Open(OpenFlags.ReadOnly);
        return store.Certificates
            .Find(X509FindType.FindByThumbprint, normalized, validOnly: false)
            .OfType<X509Certificate2>()
            .FirstOrDefault();
    }
}
