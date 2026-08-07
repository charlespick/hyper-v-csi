using System.Security.Authentication;
using HyperVCsiAgent.Core.Configuration;
using HyperVCsiAgent.Core.Security;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace HyperVCsiAgent.Service.Security;

public static class HttpsConfiguration
{
    /// <summary>
    /// Configures the HTTPS listener: a self-signed server certificate read
    /// from the Windows store and selected by pinned thumbprint, and mutual
    /// TLS against a pinned set of client certificate fingerprints.
    ///
    /// Authorization happens during the handshake rather than in middleware, so
    /// an unrecognized caller never reaches the job API at all - there is no
    /// route, header, or parsing bug that could let one through. The tradeoff
    /// is that a rejected client sees a TLS failure rather than a 403, which is
    /// why every rejection is logged with the fingerprint that was presented.
    /// </summary>
    /// <param name="options">
    /// The same instance the rest of the host resolves from DI. Kestrel has to
    /// be configured before the container exists, so it would be easy to bind a
    /// second copy here - and then the startup guards would be vouching for a
    /// listener they don't actually describe.
    /// </param>
    public static void ConfigureHttps(this WebApplicationBuilder builder, AgentOptions options)
    {
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
                    var certificates = listen.ApplicationServices.GetRequiredService<IServerCertificateProvider>();
                    var logger = listen.ApplicationServices.GetRequiredService<ILoggerFactory>()
                        .CreateLogger("HyperVCsiAgent.Service.Security.ClientCertificate");
                    var clock = listen.ApplicationServices.GetRequiredService<TimeProvider>();

                    https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;

                    // Re-read per handshake (cheap - the provider caches) so a
                    // manual rotation is picked up without a restart.
                    https.ServerCertificateSelector = (_, _) => certificates.Current;

                    if (!options.Authentication.IsConfigured)
                    {
                        return;
                    }

                    https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;

                    // Assigning this delegate is what disables the built-in
                    // chain check - Kestrel skips its own sslPolicyErrors
                    // handling entirely once a validation callback is set. That
                    // is deliberate: these certificates are self-signed, so
                    // there is no chain to validate and the built-in check
                    // would reject every one of them. The fingerprint pin below
                    // is the whole of the authorization, so this delegate must
                    // never be replaced by anything more permissive -
                    // AllowAnyClientCertificate() in particular assigns a
                    // callback that returns true for everything, and setting it
                    // after this line would accept any certificate at all.
                    // ClientCertificateEnforcementTests guards exactly that.
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
