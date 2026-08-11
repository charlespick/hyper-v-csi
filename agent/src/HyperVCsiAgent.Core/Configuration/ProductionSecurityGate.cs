using Microsoft.Extensions.Logging;

namespace HyperVCsiAgent.Core.Configuration;

/// <summary>
/// Running without TLS or without client authentication is a development
/// convenience, never a deployment state. Anything that can reach an unsecured
/// agent can create and delete volumes on the CSV, so refuse to start rather
/// than let a misconfigured deployment come up quietly serving plaintext.
/// </summary>
public static class ProductionSecurityGate
{
    public static void Enforce(bool isDevelopment, AgentOptions options, ILogger logger)
    {
        if (!isDevelopment)
        {
            if (!options.Tls.IsConfigured)
            {
                throw new InvalidOperationException(
                    $"{AgentOptions.SectionName}:Tls:AllowedThumbprints is required outside Development; the agent must not serve plaintext HTTP");
            }

            if (!options.Authentication.IsConfigured)
            {
                throw new InvalidOperationException(
                    $"{AgentOptions.SectionName}:Authentication:AllowedClientCertificateThumbprints is required outside Development; " +
                    "an agent without client authentication lets anything that can reach it provision and delete volumes");
            }
        }
        else if (!options.Tls.IsConfigured || !options.Authentication.IsConfigured)
        {
            logger.LogWarning(
                "running without {Missing}. This is Development only - any caller that can reach this agent can create and delete volumes",
                !options.Tls.IsConfigured ? "TLS" : "client certificate authentication");
        }
    }
}
