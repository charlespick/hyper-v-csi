using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using HyperVCsiAgent.Core.Configuration;
using HyperVCsiAgent.Core.Security;
using HyperVCsiAgent.Service.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HyperVCsiAgent.Service.Tests;

/// <summary>
/// StoreCertificateProviderTests pins the caching/reload logic against an
/// injected loader and never calls the real LoadFromStore. Client
/// CertificateEnforcementTests boots real Kestrel but with a certificate that
/// was never round-tripped through an X509Store. Neither exercises the
/// certificate StoreCertificateProvider actually hands Kestrel in production:
/// one pulled from a real store and cloned the way LoadFromStore clones it.
/// That gap is exactly how a bad clone strategy shipped - Linux's OpenSSL
/// backend happily terminates TLS with an ephemeral key, but Schannel, the
/// only TLS backend this agent runs against in production, refuses one
/// outright, and nothing here walked that path against a live handshake.
/// </summary>
public sealed class StoreCertificateProviderIntegrationTests : IAsyncLifetime
{
    private readonly X509Certificate2 _certificate = SelfSigned("agent.store.test");
    private WebApplication _app = null!;
    private string _baseAddress = null!;

    public async Task InitializeAsync()
    {
        using (var store = new X509Store(StoreName.My, StoreLocation.CurrentUser))
        {
            store.Open(OpenFlags.ReadWrite);
            store.Add(_certificate);
        }

        var options = new AgentOptions
        {
            CsvVolumesRoot = Path.GetTempPath(),
            Tls =
            {
                SubjectName = "agent.store.test",
                StoreName = "My",
                StoreLocation = "CurrentUser",
                Port = 0,
            },
        };

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseSetting("urls", string.Empty);
        builder.Services.AddSingleton(Options.Create(options));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<StoreCertificateProvider>();
        builder.Services.AddSingleton<IServerCertificateProvider>(
            services => services.GetRequiredService<StoreCertificateProvider>());
        builder.ConfigureHttps(options);

        _app = builder.Build();
        _app.MapGet("/healthz", () => Results.Ok());

        // Same ordering as Program.cs: read the store before serving, so a
        // bad certificate fails setup rather than the first handshake.
        _app.Services.GetRequiredService<StoreCertificateProvider>().Warmup();

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

        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Remove(_certificate);
        _certificate.Dispose();
    }

    [Fact]
    public async Task CertificateLoadedFromTheStore_CompletesARealHandshake()
    {
        // The regression this guards: StoreCertificateProvider re-exporting
        // the selected certificate with X509KeyStorageFlags.EphemeralKeySet
        // produced a key Schannel refuses to use as a TLS server credential,
        // failing every handshake with "the platform does not support
        // ephemeral keys" - on Windows, the only OS this agent ships on.
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

        var response = await client.GetAsync($"{_baseAddress}/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static X509Certificate2 SelfSigned(string commonName)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={commonName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

        // Exportable so it can be Add()-ed to the store and actually persists
        // a private key backing it - the same shape a certbot-issued
        // certificate has once certbot imports it.
        return X509CertificateLoader.LoadPkcs12(
            certificate.Export(X509ContentType.Pkcs12), password: null, X509KeyStorageFlags.Exportable);
    }
}
