using Microsoft.AspNetCore.Server.Kestrel.Https;
using HyperVCsiAgent.Core.Configuration;
using HyperVCsiAgent.Core.Security;
using Microsoft.Extensions.Options;

namespace HyperVCsiAgent.Service.Security;

public static class HttpsConfiguration
{
    /// <summary>
    /// Configures the HTTPS listener: a Let's Encrypt server certificate read
    /// from the Windows store, and mutual TLS against a pinned set of client
    /// certificate fingerprints.
    ///
    /// Authorization happens during the handshake rather than in middleware, so
    /// an unrecognized caller never reaches the job API at all - there is no
    /// route, header, or parsing bug that could let one through. The tradeoff
    /// is that a rejected client sees a TLS failure rather than a 403, which is
    /// why every rejection is logged with the fingerprint that was presented.
    /// </summary>
    public static void ConfigureHttps(this WebApplicationBuilder builder)
    {
        var options = builder.Configuration
            .GetSection(AgentOptions.SectionName)
            .Get<AgentOptions>() ?? new AgentOptions();

        if (!options.Tls.IsConfigured)
        {
            return;
        }

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.ListenAnyIP(options.Tls.Port, listen =>
            {
                listen.UseHttps(https =>
                {
                    var certificates = listen.ApplicationServices.GetRequiredService<StoreCertificateProvider>();
                    var logger = listen.ApplicationServices.GetRequiredService<ILoggerFactory>()
                        .CreateLogger("HyperVCsiAgent.Service.Security.ClientCertificate");
                    var clock = listen.ApplicationServices.GetRequiredService<TimeProvider>();

                    // Re-read per handshake (cheap - the provider caches) so a
                    // certbot renewal is picked up without a restart.
                    https.ServerCertificateSelector = (_, _) => certificates.Current;

                    if (!options.Authentication.IsConfigured)
                    {
                        return;
                    }

                    https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;

                    // The client certificates are self-signed, so the built-in
                    // chain validation would reject every one of them. The
                    // fingerprint pin replaces it outright.
                    https.AllowAnyClientCertificate();
                    https.ClientCertificateValidation = (certificate, _, _) =>
                    {
                        var allowed = ClientCertificateAuthenticator.IsAllowed(
                            certificate, options.Authentication.AllowedClientCertificateThumbprints, clock.GetUtcNow());

                        if (!allowed)
                        {
                            logger.LogWarning(
                                "rejected client certificate {Thumbprint} ({Subject}), expiring {NotAfter}: not a pinned fingerprint, or outside its validity window",
                                certificate.Thumbprint, certificate.Subject, certificate.NotAfter);
                        }

                        return allowed;
                    };
                });
            });
        });
    }
}
