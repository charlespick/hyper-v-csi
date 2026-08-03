using System.Security.Cryptography.X509Certificates;

namespace HyperVCsiAgent.Core.Configuration;

/// <summary>
/// How the agent finds its HTTPS server certificate. Deliberately selects by
/// subject rather than thumbprint: the certificates are issued by Let's Encrypt
/// and rotate roughly every two months, and a thumbprint would pin to one
/// specific issuance and break at the first renewal.
/// </summary>
public sealed class TlsOptions
{
    /// <summary>
    /// The DNS name the clustered role answers on, matched against each
    /// candidate certificate's subject CN and its DNS subject alternative
    /// names. Empty means TLS is not configured, which is only allowed in
    /// Development.
    /// </summary>
    public string SubjectName { get; set; } = string.Empty;

    public string StoreName { get; set; } = "My";

    public string StoreLocation { get; set; } = "LocalMachine";

    /// <summary>
    /// How often to re-read the certificate store. Renewal is picked up on the
    /// next connection after this elapses, so certbot replacing the
    /// certificate doesn't need the clustered role restarted - which would
    /// otherwise mean a deliberate outage every couple of months.
    /// </summary>
    public TimeSpan ReloadInterval { get; set; } = TimeSpan.FromHours(1);

    public int Port { get; set; } = 443;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(SubjectName);

    /// <summary>
    /// Parsed store identity. Parsing here rather than at the point of use
    /// means a typo fails the role at startup - loudly, where the cluster
    /// reports it - instead of throwing inside every TLS handshake while the
    /// service still reports itself Online.
    /// </summary>
    public (StoreName Name, StoreLocation Location) ResolveStore()
    {
        if (!Enum.TryParse<StoreName>(StoreName, ignoreCase: true, out var name))
        {
            throw new InvalidOperationException(
                $"{AgentOptions.SectionName}:Tls:{nameof(StoreName)} '{StoreName}' is not a certificate store name; " +
                $"expected one of {string.Join(", ", Enum.GetNames<StoreName>())}");
        }

        if (!Enum.TryParse<StoreLocation>(StoreLocation, ignoreCase: true, out var location))
        {
            throw new InvalidOperationException(
                $"{AgentOptions.SectionName}:Tls:{nameof(StoreLocation)} '{StoreLocation}' is not a certificate store location; " +
                $"expected one of {string.Join(", ", Enum.GetNames<StoreLocation>())}");
        }

        return (name, location);
    }

    public void Validate()
    {
        if (!IsConfigured)
        {
            return;
        }

        ResolveStore();

        if (ReloadInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{AgentOptions.SectionName}:Tls:{nameof(ReloadInterval)} must be positive");
        }

        if (Port is < 1 or > 65535)
        {
            throw new InvalidOperationException(
                $"{AgentOptions.SectionName}:Tls:{nameof(Port)} must be a valid port number");
        }
    }
}
