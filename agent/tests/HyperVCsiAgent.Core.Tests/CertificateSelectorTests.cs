using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using HyperVCsiAgent.Core.Security;

namespace HyperVCsiAgent.Core.Tests;

/// <summary>
/// The agent's server certificate is self-signed and rotated by hand, exactly
/// like the driver's client certificate. These pin the behaviour that makes a
/// rotation - list both thumbprints, install the new certificate, remove the
/// old one - survivable without an operator restarting the clustered role.
/// </summary>
public class CertificateSelectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Select_DuringARotation_PrefersTheLongerLivedCertificate()
    {
        // Both thumbprints are listed during a rotation window; picking the
        // one that lasts longest means the switchover happens on its own
        // rather than at whatever moment the old one is removed.
        using var outgoing = SelfSigned(Now.AddDays(-60), Now.AddDays(30));
        using var incoming = SelfSigned(Now.AddDays(-1), Now.AddDays(89));

        var selected = CertificateSelector.Select(
            [outgoing, incoming], [outgoing.Thumbprint, incoming.Thumbprint], Now);

        Assert.Equal(incoming.Thumbprint, selected?.Thumbprint);
    }

    [Fact]
    public void Select_AcceptsThumbprintsInWhateverFormatWasPasted()
    {
        // Same tolerance as the client-certificate pin: operators copy these
        // out of certutil, openssl, and the Windows certificate dialog.
        using var certificate = SelfSigned(Now.AddDays(-1), Now.AddDays(89));
        var colonSeparated = string.Join(':', Enumerable
            .Range(0, certificate.Thumbprint.Length / 2)
            .Select(i => certificate.Thumbprint.Substring(i * 2, 2)));

        Assert.NotNull(CertificateSelector.Select([certificate], [colonSeparated], Now));
    }

    [Fact]
    public void Select_IgnoresCertificatesNotInTheAllowedSet()
    {
        using var other = SelfSigned(Now.AddDays(-1), Now.AddDays(89));

        Assert.Null(CertificateSelector.Select([other], ["6831285AB162AC3C472B39EC196A0F06D67B2A52"], Now));
    }

    [Theory]
    [InlineData(-120, -30)] // already expired
    [InlineData(10, 100)] // not valid yet
    public void Select_IgnoresCertificatesOutsideTheirValidityWindow(int fromDays, int toDays)
    {
        using var certificate = SelfSigned(Now.AddDays(fromDays), Now.AddDays(toDays));

        Assert.Null(CertificateSelector.Select([certificate], [certificate.Thumbprint], Now));
    }

    [Fact]
    public void Select_IgnoresCertificatesWithoutAPrivateKey()
    {
        // The chain often includes the public certificate on its own; it can't
        // terminate TLS, so selecting it would break every handshake.
        using var withKey = SelfSigned(Now.AddDays(-1), Now.AddDays(89));
        using var publicOnly = X509CertificateLoader.LoadCertificate(withKey.Export(X509ContentType.Cert));

        Assert.Null(CertificateSelector.Select([publicOnly], [withKey.Thumbprint], Now));
    }

    [Fact]
    public void Select_NoCandidates_ReturnsNull()
    {
        Assert.Null(CertificateSelector.Select([], ["6831285AB162AC3C472B39EC196A0F06D67B2A52"], Now));
    }

    [Fact]
    public void Select_NothingAllowed_ReturnsNull()
    {
        // Fails closed: an empty allow-list must never mean "serve anything".
        using var certificate = SelfSigned(Now.AddDays(-1), Now.AddDays(89));

        Assert.Null(CertificateSelector.Select([certificate], [], Now));
    }

    private static X509Certificate2 SelfSigned(DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN=hyperv-csi-agent-{Guid.NewGuid():n}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(notBefore, notAfter);
    }
}
