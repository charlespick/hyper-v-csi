using HyperVCsiAgent.Core.Security;

namespace HyperVCsiAgent.Core.Configuration;

/// <summary>
/// Callers authenticate with a client certificate, pinned here by fingerprint.
/// The certificate is self-signed and lives in a Kubernetes Secret, so there is
/// no CA to run and no trust chain to get wrong - a caller is authorized if and
/// only if it proves possession of a private key whose certificate is listed
/// below.
///
/// <see cref="TlsOptions"/> pins the agent's own server certificate the same
/// way, for the same reason: both certificates are minted by hand and rotate
/// only when someone decides to, so a fingerprint pin is the whole of the
/// verification on both sides.
/// </summary>
public sealed class AuthenticationOptions
{
    /// <summary>
    /// SHA-1 thumbprints of the client certificates allowed to call this agent.
    /// Accepts the usual formats - with or without colons or spaces, any case.
    /// More than one may be listed so a rotation can add the new certificate,
    /// roll the driver, and remove the old one without an outage. Empty means
    /// client authentication is not configured, which is only allowed in
    /// Development.
    /// </summary>
    public string[] AllowedClientCertificateThumbprints { get; set; } = [];

    public bool IsConfigured => AllowedClientCertificateThumbprints.Length > 0;

    public void Validate()
    {
        foreach (var thumbprint in AllowedClientCertificateThumbprints)
        {
            var normalized = ClientCertificateAuthenticator.Normalize(thumbprint);
            if (ClientCertificateAuthenticator.IsWellFormed(normalized))
            {
                continue;
            }

            // Caught here rather than left to fail at runtime, because the
            // symptom is otherwise baffling: a pin that looks right in the
            // config file matches nothing, and every caller is locked out with
            // a TLS error. The usual cause is pasting openssl's whole line -
            // "sha1 Fingerprint=AA:BB:..." - whose label contributes hex
            // letters of its own.
            throw new InvalidOperationException(
                $"{AgentOptions.SectionName}:Authentication:{nameof(AllowedClientCertificateThumbprints)} entry " +
                $"'{thumbprint}' is not a SHA-1 thumbprint: expected {ClientCertificateAuthenticator.ThumbprintLength} " +
                $"hex characters, got {normalized.Length}. Paste only the fingerprint, without any label.");
        }
    }
}
