using System.Net;
using System.Security.Authentication;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using HyperVCsiAgent.Core.Configuration;
using HyperVCsiAgent.Core.Security;
using HyperVCsiAgent.Service.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HyperVCsiAgent.Service.Tests;

/// <summary>
/// Boots real Kestrel through the real <see cref="HttpsConfiguration"/> and
/// tries to get in with the wrong certificate.
///
/// This exists because the enforcement is one line of configuration, and the
/// way it fails is silent: Kestrel skips its own chain validation as soon as a
/// ClientCertificateValidation delegate is assigned, so assigning a more
/// permissive one afterwards - AllowAnyClientCertificate() returns true for
/// everything - accepts every certificate on earth while still requiring one,
/// which looks entirely secure from the outside. Nothing else in the suite
/// touches Kestrel: WebApplicationFactory runs on TestServer, which has no TLS
/// layer at all. Without this test, that edit passes CI.
/// </summary>
public sealed class ClientCertificateEnforcementTests : IAsyncLifetime
{
    private static readonly DateTimeOffset NotBefore = DateTimeOffset.UtcNow.AddDays(-1);
    private static readonly DateTimeOffset NotAfter = DateTimeOffset.UtcNow.AddDays(30);

    private readonly X509Certificate2 _pinned = SelfSigned("pinned-driver", NotBefore, NotAfter);
    private readonly X509Certificate2 _impostor = SelfSigned("impostor", NotBefore, NotAfter);
    private readonly X509Certificate2 _expiredButPinned =
        SelfSigned("expired-driver", DateTimeOffset.UtcNow.AddDays(-40), DateTimeOffset.UtcNow.AddDays(-1));

    private WebApplication _app = null!;
    private string _baseAddress = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseSetting("urls", string.Empty);

        var options = new AgentOptions
        {
            CsvVolumesRoot = Path.GetTempPath(),
            Tls = { HostName = "agent.test", Port = 0 },
            Authentication =
            {
                // Both the live driver certificate and one that is pinned but
                // has expired, so the expiry check is exercised through the
                // real handshake rather than only as a unit.
                AllowedClientCertificateThumbprints = [_pinned.Thumbprint, _expiredButPinned.Thumbprint],
            },
        };

        builder.Services.AddSingleton(Options.Create(options));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IServerCertificateProvider>(
            new FixedCertificateProvider(SelfSigned("agent.test", NotBefore, NotAfter)));
        builder.ConfigureHttps(options);

        _app = builder.Build();
        _app.MapPost("/v1/jobs", () => Results.Accepted("/v1/jobs/job-1", new { id = "job-1" }));

        await _app.StartAsync();

        // Kestrel reports the wildcard it bound (https://[::]:port), which is
        // not a connectable target. Keep only the port it chose.
        var bound = new Uri(_app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First());
        _baseAddress = $"https://127.0.0.1:{bound.Port}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        _pinned.Dispose();
        _impostor.Dispose();
        _expiredButPinned.Dispose();
    }

    [Fact]
    public async Task PinnedCertificate_ReachesTheJobApi()
    {
        using var client = ClientPresenting(_pinned);

        var response = await client.PostAsJsonAsync($"{_baseAddress}/v1/jobs", new { });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task UnpinnedCertificate_NeverReachesTheJobApi()
    {
        // The whole scheme in one assertion: a caller holding a perfectly
        // valid, well-formed certificate that simply isn't the pinned one gets
        // nowhere.
        using var client = ClientPresenting(_impostor);

        await AssertRejectedDuringHandshake(client);
    }

    [Fact]
    public async Task NoCertificate_NeverReachesTheJobApi()
    {
        using var client = ClientPresenting(null);

        await AssertRejectedDuringHandshake(client);
    }

    [Fact]
    public async Task PinnedButExpiredCertificate_NeverReachesTheJobApi()
    {
        // Pinning says which key is trusted, not for how long. If expiry were
        // ignored here, rotating a client certificate would accomplish nothing.
        using var client = ClientPresenting(_expiredButPinned);

        await AssertRejectedDuringHandshake(client);
    }

    /// <summary>
    /// Asserts the request died in the TLS handshake specifically. Accepting
    /// any HttpRequestException would let these pass for reasons that have
    /// nothing to do with authorization - an unreachable address does the same
    /// thing, and a negative test that passes when the server is missing is
    /// worse than no test.
    /// </summary>
    private async Task AssertRejectedDuringHandshake(HttpClient client)
    {
        var thrown = await Assert.ThrowsAnyAsync<HttpRequestException>(
            () => client.PostAsJsonAsync($"{_baseAddress}/v1/jobs", new { }));

        for (Exception? error = thrown; error is not null; error = error.InnerException)
        {
            if (error is AuthenticationException or IOException)
            {
                return;
            }
        }

        Assert.Fail($"expected a TLS handshake failure, got {thrown}");
    }

    private static HttpClient ClientPresenting(X509Certificate2? certificate)
    {
        var handler = new HttpClientHandler
        {
            // The agent's own certificate is self-signed here; in production
            // it is publicly trusted. Server trust is not what these tests are
            // about.
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            ClientCertificateOptions = ClientCertificateOption.Manual,
        };

        if (certificate is not null)
        {
            handler.ClientCertificates.Add(certificate);
        }

        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    private static X509Certificate2 SelfSigned(string commonName, DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={commonName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2")], critical: false));

        using var certificate = request.CreateSelfSigned(notBefore, notAfter);

        // Round-trip through PKCS#12 so the private key is usable for TLS on
        // every platform the tests run on.
        return X509CertificateLoader.LoadPkcs12(
            certificate.Export(X509ContentType.Pkcs12), password: null, X509KeyStorageFlags.Exportable);
    }

    private sealed class FixedCertificateProvider(X509Certificate2 certificate) : IServerCertificateProvider
    {
        public X509Certificate2 Current { get; } = certificate;
    }
}
