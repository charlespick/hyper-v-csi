using System.Security.Cryptography.X509Certificates;

namespace HyperVCsiAgent.Core.Security;

/// <summary>
/// Decides whether a presented client certificate is one of the pinned ones.
/// Chain and issuer are deliberately not consulted: the certificates are
/// self-signed, so there is no chain worth validating, and possession of the
/// matching private key - which TLS has already proven by the time this runs -
/// is the entire claim being checked.
/// </summary>
public static class ClientCertificateAuthenticator
{
    public static bool IsAllowed(X509Certificate2? certificate, IEnumerable<string> allowedThumbprints, DateTimeOffset now)
    {
        if (certificate is null)
        {
            return false;
        }

        // An expired pinned certificate is still refused. The pin says which
        // key we trust; the validity window says for how long, and honouring it
        // is what makes rotation mean anything.
        if (now < certificate.NotBefore || now > certificate.NotAfter)
        {
            return false;
        }

        var presented = Normalize(certificate.Thumbprint);
        if (presented.Length == 0)
        {
            return false;
        }

        foreach (var allowed in allowedThumbprints)
        {
            // Ordinal comparison over normalized hex, so this is a fixed-length
            // constant-shape check rather than anything format-dependent.
            if (Normalize(allowed).Equals(presented, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Thumbprints get copied out of certutil, openssl, and the Windows
    /// certificate dialog, which format them with colons, spaces, and differing
    /// case. Normalizing means an operator can paste any of those.
    /// </summary>
    private static string Normalize(string? thumbprint)
    {
        if (string.IsNullOrWhiteSpace(thumbprint))
        {
            return string.Empty;
        }

        return string.Concat(thumbprint.Where(Uri.IsHexDigit)).ToUpperInvariant();
    }
}
