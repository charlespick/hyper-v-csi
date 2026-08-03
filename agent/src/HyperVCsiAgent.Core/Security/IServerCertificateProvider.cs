using System.Security.Cryptography.X509Certificates;

namespace HyperVCsiAgent.Core.Security;

/// <summary>
/// Supplies the certificate Kestrel serves. An interface so the TLS pipeline -
/// including client-certificate enforcement, the most security-critical code
/// here - can be exercised against an in-memory certificate, without a Windows
/// certificate store.
/// </summary>
public interface IServerCertificateProvider
{
    /// <summary>
    /// Called once per TLS handshake, so implementations must be cheap and
    /// thread-safe. Throws if no certificate can be served, which fails the
    /// handshake rather than serving something a client will reject anyway.
    /// </summary>
    X509Certificate2 Current { get; }
}
