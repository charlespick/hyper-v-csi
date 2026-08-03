using System.Security.Cryptography.X509Certificates;
using HyperVCsiAgent.Core.Configuration;
using HyperVCsiAgent.Core.Security;
using Microsoft.Extensions.Options;

namespace HyperVCsiAgent.Service.Security;

/// <summary>
/// Serves the current certificate from the Windows certificate store, re-reading
/// it periodically. certbot renews the Let's Encrypt certificate into the store
/// roughly every two months; re-reading means that renewal is picked up on the
/// next connection instead of requiring the clustered role to be restarted,
/// which would be a deliberate outage on a fixed schedule.
/// </summary>
public sealed class StoreCertificateProvider : IDisposable
{
    private readonly TlsOptions _options;
    private readonly ILogger<StoreCertificateProvider> _logger;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();

    private X509Certificate2? _current;
    private DateTimeOffset _nextReload = DateTimeOffset.MinValue;

    public StoreCertificateProvider(IOptions<AgentOptions> options, ILogger<StoreCertificateProvider> logger, TimeProvider? clock = null)
    {
        _options = options.Value.Tls;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
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

                var selected = Load(now);
                if (selected is null)
                {
                    // Keep serving the certificate we already have rather than
                    // failing every handshake: a store that momentarily has no
                    // match is far more likely to be a renewal glitch than a
                    // reason to take the whole agent offline.
                    if (_current is not null)
                    {
                        _logger.LogError(
                            "no certificate matching {SubjectName} in {StoreLocation}/{StoreName}; continuing with the previous one, which expires {NotAfter}",
                            _options.SubjectName, _options.StoreLocation, _options.StoreName, _current.NotAfter);
                        _nextReload = now + _options.ReloadInterval;
                        return _current;
                    }

                    throw new InvalidOperationException(
                        $"no valid certificate for {_options.SubjectName} with a private key in {_options.StoreLocation}/{_options.StoreName}");
                }

                if (_current is not null && !_current.Thumbprint.Equals(selected.Thumbprint, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation(
                        "certificate for {SubjectName} rotated to {Thumbprint}, valid until {NotAfter}",
                        _options.SubjectName, selected.Thumbprint, selected.NotAfter);
                    _current.Dispose();
                }

                _current = selected;
                _nextReload = now + _options.ReloadInterval;
                return _current;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _current?.Dispose();
            _current = null;
        }
    }

    private X509Certificate2? Load(DateTimeOffset now)
    {
        var storeName = Enum.Parse<StoreName>(_options.StoreName, ignoreCase: true);
        var storeLocation = Enum.Parse<StoreLocation>(_options.StoreLocation, ignoreCase: true);

        using var store = new X509Store(storeName, storeLocation);
        store.Open(OpenFlags.ReadOnly);

        var candidates = store.Certificates.OfType<X509Certificate2>().ToList();
        var selected = CertificateSelector.Select(candidates, _options.SubjectName, now);

        // The selected certificate outlives the store handle, so hand back a
        // copy and release everything else.
        var result = selected is null ? null : new X509Certificate2(selected);
        foreach (var candidate in candidates)
        {
            candidate.Dispose();
        }

        return result;
    }
}
