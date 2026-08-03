using System.Security.Cryptography.X509Certificates;

namespace HyperVCsiAgent.Core.Security;

/// <summary>
/// Picks the certificate to serve from a set of candidates. Pure so the rule
/// that matters - "survive a Let's Encrypt renewal" - can be tested without a
/// certificate store or a Windows host.
/// </summary>
public static class CertificateSelector
{
    /// <summary>
    /// Returns the certificate to serve for <paramref name="subjectName"/>, or
    /// null if none qualifies. During a renewal the store legitimately holds
    /// both the old and new certificate, so the tie-break is the one that
    /// stays valid longest - that's the freshly issued one, and it means the
    /// switchover happens on its own rather than at whatever moment the old
    /// certificate finally expires.
    /// </summary>
    public static X509Certificate2? Select(IEnumerable<X509Certificate2> candidates, string subjectName, DateTimeOffset now) =>
        candidates
            .Where(certificate => Matches(certificate, subjectName))
            // A certificate with no private key can't terminate TLS, and one
            // outside its validity window would be rejected by every client.
            .Where(certificate => certificate.HasPrivateKey)
            .Where(certificate => now >= certificate.NotBefore && now <= certificate.NotAfter)
            .OrderByDescending(certificate => certificate.NotAfter)
            .ThenByDescending(certificate => certificate.NotBefore)
            .FirstOrDefault();

    /// <summary>
    /// Matches the subject CN or any DNS subject alternative name. Modern
    /// issuers - Let's Encrypt included - put the name in the SAN and clients
    /// ignore the CN, so matching on CN alone would miss the certificate that
    /// is actually being served.
    /// </summary>
    private static bool Matches(X509Certificate2 certificate, string subjectName)
    {
        if (string.Equals(certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false), subjectName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return certificate.Extensions
            .OfType<X509SubjectAlternativeNameExtension>()
            .SelectMany(extension => extension.EnumerateDnsNames())
            .Any(dnsName => string.Equals(dnsName, subjectName, StringComparison.OrdinalIgnoreCase));
    }
}
