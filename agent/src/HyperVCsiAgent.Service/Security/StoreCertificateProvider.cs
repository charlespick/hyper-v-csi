using System.Security.Cryptography.X509Certificates;
using HyperVCsiAgent.Core.Configuration;
using HyperVCsiAgent.Core.Security;
using Microsoft.Extensions.Options;

namespace HyperVCsiAgent.Service.Security;

/// <summary>
/// Serves the current certificate from the Windows certificate store, re-reading
/// it periodically. An operator rotating the self-signed certificate installs
/// the new one into the store and adds its thumbprint to
/// <see cref="TlsOptions.AllowedThumbprints"/>; re-reading means that swap is
/// picked up on the next connection instead of requiring the clustered role to
/// be restarted.
/// </summary>
public sealed class StoreCertificateProvider : IServerCertificateProvider
{
    private readonly TlsOptions _options;
    private readonly ILogger<StoreCertificateProvider> _logger;
    private readonly TimeProvider _clock;
    private readonly Func<DateTimeOffset, X509Certificate2?> _load;
    private readonly Lock _gate = new();

    private X509Certificate2? _current;
    private DateTimeOffset _nextReload = DateTimeOffset.MinValue;

    /// <param name="loadCertificate">
    /// Overrides reading the Windows certificate store. Only tests pass this -
    /// the caching and fallback rules below decide whether the agent stays
    /// reachable across a renewal, which is worth testing somewhere other than
    /// a production Hyper-V host.
    /// </param>
    public StoreCertificateProvider(
        IOptions<AgentOptions> options,
        ILogger<StoreCertificateProvider> logger,
        TimeProvider? clock = null,
        Func<DateTimeOffset, X509Certificate2?>? loadCertificate = null)
    {
        _options = options.Value.Tls;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _load = loadCertificate ?? LoadFromStore;
    }

    /// <summary>
    /// The certificate to serve. Called per TLS handshake, so it stays cheap:
    /// the store is only re-read once the reload interval has elapsed.
    /// </summary>
    public X509Certificate2 Current
    {
        get
        {
            lock (_gate)
            {
                var now = _clock.GetUtcNow();
                if (_current is not null && now < _nextReload)
                {
                    return _current;
                }

                _nextReload = now + _options.ReloadInterval;
                var selected = _load(now);

                if (selected is null)
                {
                    return FallBack(now);
                }

                if (_current is not null && !_current.Thumbprint.Equals(selected.Thumbprint, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation(
                        "certificate rotated to {Thumbprint}, valid until {NotAfter}",
                        selected.Thumbprint, selected.NotAfter);
                }

                // The outgoing certificate is deliberately not disposed: a
                // handshake that already took a reference to it is using it
                // outside this lock, and disposing it out from under that
                // connection breaks it with an opaque transport error. It is
                // released when the last user drops it. This happens once a
                // renewal, so the garbage is trivial.
                _current = selected;
                return _current;
            }
        }
    }

    /// <summary>
    /// Reads the store once so a bad certificate configuration fails at
    /// startup. Without this the first symptom is a role the cluster reports
    /// Online whose every connection fails - the store isn't touched until the
    /// first handshake.
    /// </summary>
    public void Warmup() => _ = Current;

    private X509Certificate2 FallBack(DateTimeOffset now)
    {
        // Serving the certificate we already have rides out a momentary glitch
        // mid-renewal. But only while it is still valid: past its expiry every
        // client rejects it anyway, so continuing would turn a loud failure
        // into an agent that looks healthy and serves nothing.
        if (_current is not null && now <= _current.NotAfter)
        {
            _logger.LogError(
                "no certificate matching an allowed thumbprint (Tls:AllowedThumbprints) in {StoreLocation}/{StoreName}; " +
                "continuing with the current one, which expires {NotAfter}",
                _options.StoreLocation, _options.StoreName, _current.NotAfter);
            return _current;
        }

        _current = null;
        throw new InvalidOperationException(
            $"no valid certificate with an allowed thumbprint (Tls:AllowedThumbprints) and a private key in " +
            $"{_options.StoreLocation}/{_options.StoreName}; was it installed and its thumbprint added to the config?");
    }

    private X509Certificate2? LoadFromStore(DateTimeOffset now)
    {
        var (storeName, storeLocation) = _options.ResolveStore();

        using var store = new X509Store(storeName, storeLocation);
        store.Open(OpenFlags.ReadOnly);

        var candidates = store.Certificates.OfType<X509Certificate2>().ToList();
        var selected = CertificateSelector.Select(candidates, _options.AllowedThumbprints, now);

        // The selected certificate outlives the store handle, so hand back a
        // copy and release everything else. Cloning via the copy constructor
        // rather than a PKCS#12 export/reimport keeps the private key tied to
        // its original CNG/CAPI key container - re-importing with
        // EphemeralKeySet produced a key SChannel refuses to use as a TLS
        // server credential ("the platform does not support ephemeral keys"),
        // which broke every HTTPS handshake on the only OS this agent runs on.
        var result = selected is null ? null : new X509Certificate2(selected);

        foreach (var candidate in candidates)
        {
            candidate.Dispose();
        }

        return result;
    }
}
