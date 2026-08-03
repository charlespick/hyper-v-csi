namespace HyperVCsiAgent.Core.Configuration;

/// <summary>
/// Callers authenticate with a client certificate, pinned here by fingerprint.
/// The certificate is self-signed and lives in a Kubernetes Secret, so there is
/// no CA to run and no trust chain to get wrong - a caller is authorized if and
/// only if it proves possession of a private key whose certificate is listed
/// below.
///
/// Note this pins by fingerprint while <see cref="TlsOptions"/> deliberately
/// does not: these certificates are minted by hand and rotate only when someone
/// decides to, whereas the server's Let's Encrypt certificate rotates on its own
/// every couple of months.
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
            if (string.IsNullOrWhiteSpace(thumbprint))
            {
                throw new InvalidOperationException(
                    $"{AgentOptions.SectionName}:Authentication:{nameof(AllowedClientCertificateThumbprints)} contains an empty entry");
            }
        }
    }
}
