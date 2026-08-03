using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using HyperVCsiAgent.Core.Security;

namespace HyperVCsiAgent.Core.Tests;

public class ClientCertificateAuthenticatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void IsAllowed_PinnedFingerprint_IsAccepted()
    {
        using var certificate = SelfSigned(Now.AddDays(-1), Now.AddYears(1));

        Assert.True(ClientCertificateAuthenticator.IsAllowed(certificate, [certificate.Thumbprint], Now));
    }

    [Fact]
    public void IsAllowed_AnyOtherCertificate_IsRejected()
    {
        // Self-signed on both sides, so there is no issuer to be fooled by:
        // a certificate is either the pinned one or it is not.
        using var pinned = SelfSigned(Now.AddDays(-1), Now.AddYears(1));
        using var impostor = SelfSigned(Now.AddDays(-1), Now.AddYears(1));

        Assert.False(ClientCertificateAuthenticator.IsAllowed(impostor, [pinned.Thumbprint], Now));
    }

    [Theory]
    [InlineData("{0}")]
    [InlineData("{0} ")]
    public void IsAllowed_AcceptsThumbprintsInWhateverFormatWasPasted(string format)
    {
        // Operators copy these out of certutil, openssl, and the Windows
        // certificate dialog, which disagree on separators and case.
        using var certificate = SelfSigned(Now.AddDays(-1), Now.AddYears(1));
        var configured = string.Format(format, certificate.Thumbprint.ToLowerInvariant());

        Assert.True(ClientCertificateAuthenticator.IsAllowed(certificate, [configured], Now));
    }

    [Fact]
    public void IsAllowed_ColonSeparatedThumbprint_IsAccepted()
    {
        using var certificate = SelfSigned(Now.AddDays(-1), Now.AddYears(1));
        var colonSeparated = string.Join(':', Enumerable
            .Range(0, certificate.Thumbprint.Length / 2)
            .Select(i => certificate.Thumbprint.Substring(i * 2, 2)));

        Assert.True(ClientCertificateAuthenticator.IsAllowed(certificate, [colonSeparated], Now));
    }

    [Fact]
    public void IsAllowed_DuringARotation_AcceptsBothPinnedCertificates()
    {
        // Add the new fingerprint, roll the driver, remove the old one - the
        // window where both are listed is what makes that a non-outage.
        using var outgoing = SelfSigned(Now.AddDays(-300), Now.AddDays(60));
        using var incoming = SelfSigned(Now.AddDays(-1), Now.AddYears(1));
        string[] allowed = [outgoing.Thumbprint, incoming.Thumbprint];

        Assert.True(ClientCertificateAuthenticator.IsAllowed(outgoing, allowed, Now));
        Assert.True(ClientCertificateAuthenticator.IsAllowed(incoming, allowed, Now));
    }

    [Theory]
    [InlineData(-400, -30)] // expired
    [InlineData(10, 400)] // not valid yet
    public void IsAllowed_PinnedButOutsideItsValidityWindow_IsRejected(int fromDays, int toDays)
    {
        // The pin says which key is trusted; the validity window says for how
        // long. Ignoring the window would make an expired certificate valid
        // forever, which is the opposite of what rotating one achieves.
        using var certificate = SelfSigned(Now.AddDays(fromDays), Now.AddDays(toDays));

        Assert.False(ClientCertificateAuthenticator.IsAllowed(certificate, [certificate.Thumbprint], Now));
    }

    [Fact]
    public void IsAllowed_NoCertificatePresented_IsRejected()
    {
        Assert.False(ClientCertificateAuthenticator.IsAllowed(null, ["A1B2"], Now));
    }

    [Fact]
    public void IsAllowed_NothingPinned_RejectsEverything()
    {
        // Fails closed: an empty list must never mean "allow all".
        using var certificate = SelfSigned(Now.AddDays(-1), Now.AddYears(1));

        Assert.False(ClientCertificateAuthenticator.IsAllowed(certificate, [], Now));
    }

    private static X509Certificate2 SelfSigned(DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN=hyperv-csi-driver-{Guid.NewGuid():n}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(notBefore, notAfter);
    }
}
