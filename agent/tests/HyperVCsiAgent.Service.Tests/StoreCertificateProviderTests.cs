using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using HyperVCsiAgent.Core.Configuration;
using HyperVCsiAgent.Service.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HyperVCsiAgent.Service.Tests;

/// <summary>
/// The caching and fallback rules decide whether the agent stays reachable
/// across a Let's Encrypt renewal, which happens unattended every couple of
/// months. Tested against an injected loader rather than a real certificate
/// store so the behaviour is pinned somewhere other than production.
/// </summary>
public sealed class StoreCertificateProviderTests
{
    [Fact]
    public void Current_WithinTheReloadInterval_DoesNotHitTheStore()
    {
        // Called once per handshake, so re-reading the store every time would
        // put a certificate store lookup in front of every connection.
        using var certificate = SelfSigned(days: 60);
        var reads = 0;
        var provider = NewProvider(new FakeClock(), _ => { reads++; return certificate; });

        _ = provider.Current;
        _ = provider.Current;
        _ = provider.Current;

        Assert.Equal(1, reads);
    }

    [Fact]
    public void Current_AfterTheReloadInterval_PicksUpARenewal()
    {
        // The whole point: certbot replaces the certificate and the agent
        // starts serving it without the clustered role being restarted.
        using var oldCertificate = SelfSigned(days: 1);
        using var renewed = SelfSigned(days: 90);
        var clock = new FakeClock();
        var current = oldCertificate;
        var provider = NewProvider(clock, _ => current);

        Assert.Equal(oldCertificate.Thumbprint, provider.Current.Thumbprint);

        current = renewed;
        clock.Advance(TimeSpan.FromHours(2));

        Assert.Equal(renewed.Thumbprint, provider.Current.Thumbprint);
    }

    [Fact]
    public void Current_RenewalDoesNotDisposeTheOutgoingCertificate()
    {
        // A handshake already holding the old certificate is using it outside
        // the provider's lock. Disposing it there kills that connection with an
        // opaque transport error.
        using var oldCertificate = SelfSigned(days: 1);
        using var renewed = SelfSigned(days: 90);
        var clock = new FakeClock();
        var current = oldCertificate;
        var provider = NewProvider(clock, _ => current);

        var inFlight = provider.Current;
        current = renewed;
        clock.Advance(TimeSpan.FromHours(2));
        _ = provider.Current;

        // Would throw if the provider had disposed it.
        Assert.NotNull(inFlight.GetRSAPrivateKey());
    }

    [Fact]
    public void Current_StoreMomentarilyEmpty_KeepsServingTheValidCertificate()
    {
        // A blip mid-renewal shouldn't take the agent down while the
        // certificate in hand is still perfectly good.
        using var certificate = SelfSigned(days: 60);
        var clock = new FakeClock();
        var found = true;
        var provider = NewProvider(clock, _ => found ? certificate : null);

        _ = provider.Current;
        found = false;
        clock.Advance(TimeSpan.FromHours(2));

        Assert.Equal(certificate.Thumbprint, provider.Current.Thumbprint);
    }

    [Fact]
    public void Current_WhenTheFallbackCertificateHasExpired_FailsInsteadOfServingIt()
    {
        // Past expiry every client rejects it anyway, so continuing to serve it
        // would turn a loud failure into an agent that looks healthy and serves
        // nothing. This is the bound on the fallback above.
        using var certificate = SelfSigned(days: 5);
        var clock = new FakeClock();
        var found = true;
        var provider = NewProvider(clock, _ => found ? certificate : null);

        _ = provider.Current;
        found = false;
        clock.Advance(TimeSpan.FromDays(6));

        var failure = Assert.Throws<InvalidOperationException>(() => provider.Current);
        Assert.Contains("certbot", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Warmup_WithNoCertificate_ThrowsSoStartupFails()
    {
        // Surfaces a misconfigured store at startup, where the cluster reports
        // the role failing, instead of at the first handshake with the role
        // showing Online.
        var provider = NewProvider(new FakeClock(), _ => null);

        Assert.Throws<InvalidOperationException>(provider.Warmup);
    }

    private static StoreCertificateProvider NewProvider(
        TimeProvider clock, Func<DateTimeOffset, X509Certificate2?> load) =>
        new(
            Options.Create(new AgentOptions
            {
                CsvVolumesRoot = Path.GetTempPath(),
                Tls = { SubjectName = "agent.test", ReloadInterval = TimeSpan.FromHours(1) },
            }),
            NullLogger<StoreCertificateProvider>.Instance,
            clock,
            load);

    private static X509Certificate2 SelfSigned(int days)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=agent.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(days));
        return X509CertificateLoader.LoadPkcs12(
            certificate.Export(X509ContentType.Pkcs12), password: null, X509KeyStorageFlags.Exportable);
    }

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
