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
        if (!IsWellFormed(presented))
        {
            return false;
        }

        foreach (var allowed in allowedThumbprints)
        {
            var pin = Normalize(allowed);

            // A malformed pin can never match. Config validation rejects these
            // at startup, so reaching here means something bypassed it -
            // failing closed is the only safe reading.
            if (!IsWellFormed(pin))
            {
                continue;
            }

            if (pin.Equals(presented, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Length of a SHA-1 thumbprint in hex characters.</summary>
    public const int ThumbprintLength = 40;

    public static bool IsWellFormed(string normalizedThumbprint) =>
        normalizedThumbprint.Length == ThumbprintLength;

    /// <summary>
    /// Thumbprints get copied out of certutil, openssl, and the Windows
    /// certificate dialog, which format them with colons, spaces, and differing
    /// case. Normalizing means an operator can paste any of those.
    ///
    /// Note this strips <em>every</em> non-hex character, so a label pasted
    /// along with the value - openssl prints "sha1 Fingerprint=AA:BB:..." -
    /// contributes its own hex letters and silently lengthens the result.
    /// That's why callers must check <see cref="IsWellFormed"/> rather than
    /// trusting whatever comes back.
    /// </summary>
    public static string Normalize(string? thumbprint)
    {
        if (string.IsNullOrWhiteSpace(thumbprint))
        {
            return string.Empty;
        }

        return string.Concat(thumbprint.Where(Uri.IsHexDigit)).ToUpperInvariant();
    }
}
