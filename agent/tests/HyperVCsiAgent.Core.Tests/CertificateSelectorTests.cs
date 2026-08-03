using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using HyperVCsiAgent.Core.Security;

namespace HyperVCsiAgent.Core.Tests;

/// <summary>
/// The agent's server certificate comes from Let's Encrypt via certbot and is
/// replaced roughly every two months. These pin the behaviour that makes that
/// survivable without an operator touching the clustered role.
/// </summary>
public class CertificateSelectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);
    private const string Subject = "hyperv-csi-agent.makerland.xyz";

    [Fact]
    public void Select_DuringARenewal_PrefersTheNewCertificate()
    {
        // certbot leaves both in the store for a while. Picking the one that
        // lasts longest means the switchover happens on renewal rather than at
        // the moment the old certificate finally expires.
        using var expiringSoon = SelfSigned(Subject, Now.AddDays(-60), Now.AddDays(30));
        using var justRenewed = SelfSigned(Subject, Now.AddDays(-1), Now.AddDays(89));

        var selected = CertificateSelector.Select([expiringSoon, justRenewed], Subject, Now);

        Assert.Equal(justRenewed.Thumbprint, selected?.Thumbprint);
    }

    [Fact]
    public void Select_MatchesOnSubjectAlternativeName()
    {
        // Let's Encrypt puts the name in the SAN, and clients ignore the CN
        // entirely, so matching only the CN would miss the right certificate.
        using var certificate = SelfSigned("some-internal-name", Now.AddDays(-1), Now.AddDays(89), dnsName: Subject);

        Assert.NotNull(CertificateSelector.Select([certificate], Subject, Now));
    }

    [Fact]
    public void Select_IgnoresCertificatesForOtherNames()
    {
        using var other = SelfSigned("something-else.makerland.xyz", Now.AddDays(-1), Now.AddDays(89));

        Assert.Null(CertificateSelector.Select([other], Subject, Now));
    }

    [Theory]
    [InlineData(-120, -30)] // already expired
    [InlineData(10, 100)] // not valid yet
    public void Select_IgnoresCertificatesOutsideTheirValidityWindow(int fromDays, int toDays)
    {
        using var certificate = SelfSigned(Subject, Now.AddDays(fromDays), Now.AddDays(toDays));

        Assert.Null(CertificateSelector.Select([certificate], Subject, Now));
    }

    [Fact]
    public void Select_IgnoresCertificatesWithoutAPrivateKey()
    {
        // The chain often includes the public certificate on its own; it can't
        // terminate TLS, so selecting it would break every handshake.
        using var withKey = SelfSigned(Subject, Now.AddDays(-1), Now.AddDays(89));
        using var publicOnly = X509CertificateLoader.LoadCertificate(withKey.Export(X509ContentType.Cert));

        Assert.Null(CertificateSelector.Select([publicOnly], Subject, Now));
    }

    [Fact]
    public void Select_NoCandidates_ReturnsNull()
    {
        Assert.Null(CertificateSelector.Select([], Subject, Now));
    }

    private static X509Certificate2 SelfSigned(
        string commonName, DateTimeOffset notBefore, DateTimeOffset notAfter, string? dnsName = null)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={commonName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        if (dnsName is not null)
        {
            var subjectAlternativeName = new SubjectAlternativeNameBuilder();
            subjectAlternativeName.AddDnsName(dnsName);
            request.CertificateExtensions.Add(subjectAlternativeName.Build());
        }

        return request.CreateSelfSigned(notBefore, notAfter);
    }
}
