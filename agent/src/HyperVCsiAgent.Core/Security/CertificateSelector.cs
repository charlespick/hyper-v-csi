using System.Security.Cryptography.X509Certificates;

namespace HyperVCsiAgent.Core.Security;

/// <summary>
/// Picks the certificate to serve from a set of candidates. Pure so the rule
/// that matters - "survive an operator rotating the certificate" - can be
/// tested without a certificate store or a Windows host.
/// </summary>
public static class CertificateSelector
{
    /// <summary>
    /// Returns the certificate to serve, or null if none qualifies. A
    /// candidate qualifies by having its thumbprint listed in
    /// <paramref name="allowedThumbprints"/> - the same pinning
    /// <see cref="ClientCertificateAuthenticator"/> applies to the driver's
    /// client certificate, just selecting rather than authorizing. During a
    /// rotation the store and the config both legitimately list two
    /// certificates at once, so the tie-break is the one that stays valid
    /// longest - the freshly installed one - which means the switchover
    /// happens on its own rather than at whatever moment the old certificate
    /// is removed.
    /// </summary>
    public static X509Certificate2? Select(
        IEnumerable<X509Certificate2> candidates, IEnumerable<string> allowedThumbprints, DateTimeOffset now)
    {
        var allowed = new HashSet<string>(
            allowedThumbprints
                .Select(ClientCertificateAuthenticator.Normalize)
                .Where(ClientCertificateAuthenticator.IsWellFormed),
            StringComparer.Ordinal);

        return candidates
            .Where(certificate => allowed.Contains(ClientCertificateAuthenticator.Normalize(certificate.Thumbprint)))
            // A certificate with no private key can't terminate TLS, and one
            // outside its validity window would be rejected by every client.
            .Where(certificate => certificate.HasPrivateKey)
            .Where(certificate => now >= certificate.NotBefore && now <= certificate.NotAfter)
            .OrderByDescending(certificate => certificate.NotAfter)
            .ThenByDescending(certificate => certificate.NotBefore)
            .FirstOrDefault();
    }
}
